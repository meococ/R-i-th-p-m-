using System.Globalization;
using System.Text;

namespace BIM.DatViet.Domain;

/// <summary>
/// Resolves a footing top from measured upward horizontal face bands.
/// <para>
/// Selection is purely geometric: the footing top is the upward horizontal band that carries a
/// dominant share of the exposed area, because everything standing on the footing (stem, backwall,
/// bearing seats) has a much smaller plan footprint than the footing itself. No declared dimension
/// takes part, so a 1500 mm, 1800 mm or 2200 mm footing resolves the same way. The declared depth is
/// compared against the measured result afterwards by <see cref="MatchesDeclaredDepth"/>, which is a
/// separate call precisely so it cannot leak back into selection.
/// </para>
/// </summary>
public static class AbutmentFootingBandKernel
{
    // The numeric contract stays in Revit internal units (feet) because the caller feeds raw
    // geometry; only the operator-facing diagnostic is converted, so no feet value reaches the UI.
    private const double MillimetresPerFoot = 304.8;
    private const double SquareMetresPerSquareFoot = 0.09290304;

    /// <summary>Keeps a failure message readable when a model produces dozens of ledges.</summary>
    private const int MaximumReportedBands = 12;

    /// <summary>
    /// One nanometre, expressed in feet. A difference that is exactly the declared tolerance has to
    /// read as "within tolerance"; without this guard the double representation of the same
    /// millimetre value on both sides of the comparison decides it. Six orders of magnitude below
    /// any fabrication tolerance, so it can never widen a real gate.
    /// </summary>
    private const double ComparisonEpsilon = 1e-6 / MillimetresPerFoot;

    /// <summary>
    /// The runner-up band may hold at most this share of the winner before the winner stops being
    /// clearly dominant. Half means the footing top must be at least twice the largest face above
    /// it, which holds for any footing that actually spreads load: the stem and backwall would have
    /// to cover more than a third of the footprint to break it, and then there is no cantilever left
    /// to reinforce. The Cầu Vạn Củi pilot measures 74.286 m² against 15.883 m², a factor of 4.7.
    /// </summary>
    public const double DominantAreaShare = 0.50;

    /// <summary>
    /// Above this share the two bands are the same size for practical purposes and the kernel
    /// refuses to guess which one is the footing top.
    /// </summary>
    public const double AmbiguousAreaShare = 0.85;

    public readonly record struct HorizontalFaceBand(double Elevation, double ExposedArea);

    /// <summary>
    /// What the caller actually scanned to produce the bands. A failure has to name the
    /// measurement, otherwise "no band matched" cannot be told apart from "nothing was measured".
    /// </summary>
    public readonly record struct FootingTopScan(
        int SolidCount,
        int PlanarFaceCount,
        int UpwardFaceCount,
        double SoffitBoundaryArea);

    /// <summary>How clearly the winning band beat the next one.</summary>
    public enum FootingTopMargin
    {
        /// <summary>No other band reaches <see cref="DominantAreaShare"/> of the winner.</summary>
        Dominant,

        /// <summary>Another band is close enough that the operator should be told.</summary>
        Narrow
    }

    /// <summary>
    /// Everything measured about the chosen band, so the caller can report the decision without
    /// re-deriving any of it.
    /// </summary>
    public readonly record struct FootingTopResolution(
        double TopElevation,
        double SoffitElevation,
        double MeasuredDepth,
        double ExposedArea,
        int CandidateCount,
        FootingTopMargin Margin,
        bool HasRunnerUp,
        double RunnerUpElevation,
        double RunnerUpExposedArea,
        bool HasLowerCandidate,
        double LowerCandidateElevation,
        double LowerCandidateExposedArea);

    private enum BandRejection
    {
        None,
        NotMeasurable,
        BelowSoffit,
        AreaTooSmall,
        TooFarForFooting
    }

    private readonly record struct EvaluatedBand(
        HorizontalFaceBand Band,
        double Rise,
        BandRejection Rejection);

    /// <summary>
    /// Upper rise that can still be a footing rather than the stem crown. Declared depth is only
    /// a sanity cap here — never a target window — so a modelled 1800 mm footing still resolves
    /// against a drawing that says 2000 mm.
    /// </summary>
    public static double ReasonableRiseCap(double expectedDepth)
    {
        if (!double.IsFinite(expectedDepth) || expectedDepth <= 0)
            return double.PositiveInfinity;
        return Math.Max(expectedDepth, 1.5 * expectedDepth);
    }

    /// <summary>
    /// Picks the footing top from measured bands alone. <paramref name="minimumExposedArea"/> must
    /// itself be derived from measured geometry (a share of the soffit area), never from a profile
    /// dimension. <paramref name="maximumReasonableRise"/> is an optional stem-distance cap
    /// (typically <see cref="ReasonableRiseCap"/>); omit it and selection ignores declared depth.
    /// </summary>
    public static bool TryResolveTop(
        double soffitElevation,
        double minimumExposedArea,
        IReadOnlyCollection<HorizontalFaceBand> measuredBands,
        FootingTopScan scan,
        out FootingTopResolution resolution,
        out string error,
        double maximumReasonableRise = double.PositiveInfinity)
    {
        resolution = default;
        error = string.Empty;
        if (!double.IsFinite(soffitElevation) ||
            !double.IsFinite(minimumExposedArea) || minimumExposedArea < 0)
        {
            error = "Footing band inputs are invalid.";
            return false;
        }

        var riseCap = double.IsFinite(maximumReasonableRise) && maximumReasonableRise >= 0
            ? maximumReasonableRise
            : double.PositiveInfinity;
        var evaluated = measuredBands
            .Select(band => Evaluate(band, soffitElevation, minimumExposedArea, riseCap))
            .ToList();
        var candidates = evaluated
            .Where(item => item.Rejection == BandRejection.None)
            .OrderByDescending(item => item.Band.ExposedArea)
            .ThenBy(item => item.Band.Elevation)
            .ToList();

        if (candidates.Count == 0)
        {
            error = DescribeFailure(soffitElevation, minimumExposedArea, riseCap, scan, evaluated);
            return false;
        }

        var winner = candidates[0];
        var hasRunnerUp = candidates.Count > 1 &&
                          Math.Abs(candidates[1].Band.Elevation - winner.Band.Elevation) > ComparisonEpsilon;
        var runnerUp = hasRunnerUp ? candidates[1] : default;
        if (hasRunnerUp && runnerUp.Band.ExposedArea > winner.Band.ExposedArea * AmbiguousAreaShare)
        {
            error = DescribeAmbiguity(winner, runnerUp, scan);
            return false;
        }

        var lower = candidates
            .Where(item => item.Band.Elevation < winner.Band.Elevation - ComparisonEpsilon)
            .OrderBy(item => item.Band.Elevation)
            .ToList();
        resolution = new FootingTopResolution(
            TopElevation: winner.Band.Elevation,
            SoffitElevation: soffitElevation,
            MeasuredDepth: winner.Rise,
            ExposedArea: winner.Band.ExposedArea,
            CandidateCount: candidates.Count,
            Margin: hasRunnerUp && runnerUp.Band.ExposedArea > winner.Band.ExposedArea * DominantAreaShare
                ? FootingTopMargin.Narrow
                : FootingTopMargin.Dominant,
            HasRunnerUp: hasRunnerUp,
            RunnerUpElevation: hasRunnerUp ? runnerUp.Band.Elevation : double.NaN,
            RunnerUpExposedArea: hasRunnerUp ? runnerUp.Band.ExposedArea : double.NaN,
            HasLowerCandidate: lower.Count > 0,
            LowerCandidateElevation: lower.Count > 0 ? lower[0].Band.Elevation : double.NaN,
            LowerCandidateExposedArea: lower.Count > 0 ? lower[0].Band.ExposedArea : double.NaN);
        return true;
    }

    /// <summary>
    /// Reports whether the measured depth agrees with the profile declaration. A deviation of
    /// exactly <paramref name="depthTolerance"/> satisfies "deviation ≤ tolerance" and is accepted.
    /// </summary>
    public static bool MatchesDeclaredDepth(
        double measuredDepth, double expectedDepth, double depthTolerance)
    {
        if (!double.IsFinite(measuredDepth) || !double.IsFinite(expectedDepth) || expectedDepth <= 0 ||
            !double.IsFinite(depthTolerance) || depthTolerance < 0)
            return false;
        return Math.Abs(measuredDepth - expectedDepth) <= depthTolerance + ComparisonEpsilon;
    }

    /// <summary>Names the chosen band and the evidence that made it the winner.</summary>
    public static string DescribeSelection(FootingTopResolution resolution, FootingTopScan scan)
    {
        var message = new StringBuilder();
        message.Append($"Đã chốt mặt trên bệ ở cao độ {Mm(resolution.TopElevation)} mm, ")
            .Append($"cách đáy {Mm(resolution.MeasuredDepth)} mm, ")
            .Append($"diện tích lộ ra {M2(resolution.ExposedArea)} m²");
        if (double.IsFinite(scan.SoffitBoundaryArea) && scan.SoffitBoundaryArea > 0)
            message.Append($" = {Percent(resolution.ExposedArea / scan.SoffitBoundaryArea)}% diện tích đáy ")
                .Append($"{M2(scan.SoffitBoundaryArea)} m²");
        message.Append(". Tiêu chí là hình học tự thân: mặt ngang hướng lên có diện tích lộ ra lớn nhất ")
            .Append("(thân, tường đỉnh và vai kê đều nhỏ hơn nhiều), không dùng footingDepthMm để chọn. ");
        message.Append(resolution.HasRunnerUp
            ? $"Ứng viên lớn thứ nhì ở cao độ {Mm(resolution.RunnerUpElevation)} mm " +
              $"chỉ có {M2(resolution.RunnerUpExposedArea)} m² " +
              $"({Percent(resolution.RunnerUpExposedArea / resolution.ExposedArea)}% của band đã chọn)."
            : "Không có ứng viên nào khác đạt ngưỡng diện tích.");
        return message.ToString();
    }

    /// <summary>
    /// Message for a winner that beat the runner-up but not by the full dominance margin. The band
    /// is still used; the operator is told so a wrong pick cannot pass unseen.
    /// </summary>
    public static string DescribeNarrowMargin(FootingTopResolution resolution) =>
        $"Mặt trên bệ đã chọn ở cao độ {Mm(resolution.TopElevation)} mm " +
        $"({M2(resolution.ExposedArea)} m²) chỉ trội hơn ứng viên ở cao độ " +
        $"{Mm(resolution.RunnerUpElevation)} mm ({M2(resolution.RunnerUpExposedArea)} m²) " +
        $"{Ratio(resolution.ExposedArea / resolution.RunnerUpExposedArea)} lần, chưa đạt mức trội " +
        $"{Ratio(1 / DominantAreaShare)} lần. Vẫn dùng band lớn nhất, nhưng hãy kiểm mắt thường " +
        "xem đúng là mặt trên bệ móng.";

    /// <summary>
    /// Message for an eligible band sitting below the winner. A plain prismatic footing has none, so
    /// this is either a stepped footing or a recess and the operator has to look.
    /// </summary>
    public static string DescribeLowerCandidate(FootingTopResolution resolution) =>
        $"Có mặt ngang hướng lên nằm THẤP HƠN mặt trên bệ đã chọn: cao độ " +
        $"{Mm(resolution.LowerCandidateElevation)} mm, lộ ra " +
        $"{M2(resolution.LowerCandidateExposedArea)} m², trong khi band đã chọn ở " +
        $"{Mm(resolution.TopElevation)} mm với {M2(resolution.ExposedArea)} m². " +
        "Bệ phẳng không có band nào như vậy; hãy kiểm xem bệ có giật cấp hoặc có hốc lõm không.";

    /// <summary>
    /// Message comparing the measured depth with the profile declaration. Reported either way, so
    /// the receipt records the agreement as well as the deviation.
    /// </summary>
    public static string DescribeDeclaredDepth(
        FootingTopResolution resolution, double expectedDepth, double depthTolerance)
    {
        var deviation = resolution.MeasuredDepth - expectedDepth;
        var matches = MatchesDeclaredDepth(resolution.MeasuredDepth, expectedDepth, depthTolerance);
        var head = matches
            ? "Chiều dày bệ đo được khớp khai báo: "
            : "CHIỀU DÀY BỆ ĐO ĐƯỢC LỆCH KHAI BÁO: ";
        var tail = matches
            ? "Không ảnh hưởng đường thép — bốn lớp thép bám mặt trên và mặt dưới đo được."
            : "Không chặn Analyze: đường thép bám mặt trên và mặt dưới ĐO ĐƯỢC, không dùng số khai. " +
              "Nhưng model đang lệch bản vẽ, hãy đối chiếu hồ sơ rồi sửa footingDepthMm hoặc sửa model.";
        return head +
               $"đo được {Mm(resolution.MeasuredDepth)} mm " +
               $"(đáy {Mm(resolution.SoffitElevation)} mm → mặt trên {Mm(resolution.TopElevation)} mm), " +
               $"footingDepthMm khai {Mm(expectedDepth)} mm, " +
               $"lệch {MmSigned(deviation)} mm so với dung sai {Mm(depthTolerance)} mm. " + tail;
    }

    /// <summary>
    /// Mirrors the acceptance chain exactly, in the same order, so recording a reason cannot
    /// change which bands are accepted.
    /// </summary>
    private static EvaluatedBand Evaluate(
        HorizontalFaceBand band,
        double soffitElevation,
        double minimumExposedArea,
        double maximumReasonableRise)
    {
        if (!double.IsFinite(band.Elevation) || !double.IsFinite(band.ExposedArea))
            return new EvaluatedBand(band, double.NaN, BandRejection.NotMeasurable);

        var rise = band.Elevation - soffitElevation;
        if (band.Elevation <= soffitElevation)
            return new EvaluatedBand(band, rise, BandRejection.BelowSoffit);
        if (band.ExposedArea < minimumExposedArea)
            return new EvaluatedBand(band, rise, BandRejection.AreaTooSmall);
        // Inclusive: a band whose rise equals the cap is still a footing, not the stem.
        if (double.IsFinite(maximumReasonableRise) &&
            rise > maximumReasonableRise + ComparisonEpsilon)
            return new EvaluatedBand(band, rise, BandRejection.TooFarForFooting);
        return new EvaluatedBand(band, rise, BandRejection.None);
    }

    private static string DescribeAmbiguity(
        EvaluatedBand winner, EvaluatedBand runnerUp, FootingTopScan scan)
    {
        var message = new StringBuilder();
        message.Append("Hai mặt ngang hướng lên có diện tích lộ ra xấp xỉ nhau nên không quyết được ")
            .Append("đâu là mặt trên bệ móng; fail-closed thay vì đoán. ")
            .Append($"Ứng viên A: cao độ {Mm(winner.Band.Elevation)} mm, cách đáy {Mm(winner.Rise)} mm, ")
            .Append($"lộ ra {M2(winner.Band.ExposedArea)} m². ")
            .Append($"Ứng viên B: cao độ {Mm(runnerUp.Band.Elevation)} mm, cách đáy {Mm(runnerUp.Rise)} mm, ")
            .Append($"lộ ra {M2(runnerUp.Band.ExposedArea)} m². ")
            .Append($"B bằng {Percent(runnerUp.Band.ExposedArea / winner.Band.ExposedArea)}% của A, ")
            .Append($"vượt ngưỡng {Percent(AmbiguousAreaShare)}% mà trên đó hai band coi như bằng nhau. ")
            .Append(DescribeScan(scan)).Append('.');
        return message.ToString();
    }

    private static string DescribeFailure(
        double soffitElevation,
        double minimumExposedArea,
        double maximumReasonableRise,
        FootingTopScan scan,
        IReadOnlyList<EvaluatedBand> evaluated)
    {
        var message = new StringBuilder();
        if (evaluated.Count == 0)
        {
            message.Append("Không đo được mặt phẳng ngang hướng lên nào trên khối mố nên không có cao độ " +
                           "top móng thật để chốt. ");
            message.Append(DescribeScan(scan)).Append(", không gom được band nào. ");
            message.Append(DescribeCriterion(soffitElevation, maximumReasonableRise));
            message.Append(' ').Append(DescribeAreaThreshold(minimumExposedArea, scan));
            return message.ToString();
        }

        message.Append("Không có mặt ngang hướng lên nào đủ điều kiện làm mặt trên bệ móng. ");
        message.Append(DescribeCriterion(soffitElevation, maximumReasonableRise));
        message.Append(' ').Append(DescribeAreaThreshold(minimumExposedArea, scan));
        message.Append(' ').Append(DescribeScan(scan))
            .Append($", gom được {evaluated.Count} band.");

        var ordered = evaluated
            .OrderByDescending(item =>
                double.IsFinite(item.Band.ExposedArea) ? item.Band.ExposedArea : double.MinValue)
            .ThenBy(item => double.IsFinite(item.Band.Elevation) ? item.Band.Elevation : double.MaxValue)
            .ToList();
        var shown = Math.Min(MaximumReportedBands, ordered.Count);
        var omitted = ordered.Count - shown;
        message.Append($" Liệt kê {shown} band lộ ra lớn nhất");
        message.Append(omitted > 0 ? $" (đã lược bớt {omitted} band nhỏ hơn):" : ":");
        for (var index = 0; index < shown; index++)
        {
            var item = ordered[index];
            message.Append($"\n  {index + 1}) cao độ {Mm(item.Band.Elevation)} mm, cách đáy {Mm(item.Rise)} mm, " +
                           $"lộ ra {M2(item.Band.ExposedArea)} m² " +
                           $"— LOẠI: {DescribeRejection(item, minimumExposedArea)}");
        }

        message.Append("\n  Tổng kết loại band: ")
            .Append($"{Count(evaluated, BandRejection.BelowSoffit)} band dưới hoặc ngang cao độ đáy, ")
            .Append($"{Count(evaluated, BandRejection.AreaTooSmall)} band thiếu diện tích lộ ra, ")
            .Append($"{Count(evaluated, BandRejection.TooFarForFooting)} band quá xa đáy (trùng thân/đỉnh), ")
            .Append($"{Count(evaluated, BandRejection.NotMeasurable)} band số đo không hợp lệ.");
        return message.ToString();
    }

    private static int Count(IReadOnlyList<EvaluatedBand> evaluated, BandRejection rejection) =>
        evaluated.Count(item => item.Rejection == rejection);

    private static string DescribeCriterion(double soffitElevation, double maximumReasonableRise)
    {
        var cap = double.IsFinite(maximumReasonableRise)
            ? $" Band cách đáy hơn {Mm(maximumReasonableRise)} mm bị coi là thân/đỉnh, không phải mặt trên bệ."
            : string.Empty;
        return $"Cao độ đáy đang dùng {Mm(soffitElevation)} mm; mặt trên bệ là mặt ngang hướng lên nằm trên " +
               "cao độ đáy và có diện tích lộ ra lớn nhất. Không dùng footingDepthMm để loại ứng viên." +
               cap;
    }

    private static string DescribeAreaThreshold(double minimumExposedArea, FootingTopScan scan)
    {
        var threshold = $"Ngưỡng diện tích lộ ra tối thiểu {M2(minimumExposedArea)} m²";
        if (!double.IsFinite(scan.SoffitBoundaryArea) || scan.SoffitBoundaryArea <= 0)
            return threshold + ".";
        var percent = Percent(minimumExposedArea / scan.SoffitBoundaryArea);
        return threshold + $" = {percent}% của diện tích đáy {M2(scan.SoffitBoundaryArea)} m².";
    }

    private static string DescribeScan(FootingTopScan scan) =>
        $"Đã quét {scan.SolidCount} solid, {scan.PlanarFaceCount} mặt phẳng, " +
        $"{scan.UpwardFaceCount} mặt ngang hướng lên";

    private static string DescribeRejection(
        EvaluatedBand item, double minimumExposedArea) => item.Rejection switch
        {
            BandRejection.BelowSoffit =>
                "band nằm dưới hoặc ngang cao độ đáy nên không thể là mặt trên bệ móng.",
            BandRejection.AreaTooSmall =>
                $"diện tích lộ ra {M2(item.Band.ExposedArea)} m² nhỏ hơn ngưỡng " +
                $"{M2(minimumExposedArea)} m².",
            BandRejection.TooFarForFooting =>
                $"cách đáy {Mm(item.Rise)} mm, quá xa để còn là mặt trên bệ (trùng thân/đỉnh).",
            BandRejection.NotMeasurable =>
                "cao độ hoặc diện tích của band không phải số hữu hạn.",
            _ => "band hợp lệ."
        };

    private static string Mm(double feet)
    {
        if (!double.IsFinite(feet)) return "không đo được";
        var value = Math.Round(feet * MillimetresPerFoot, 1);
        if (value == 0) value = 0;
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string MmSigned(double feet)
    {
        if (!double.IsFinite(feet)) return "không đo được";
        var value = Math.Round(feet * MillimetresPerFoot, 1);
        if (value == 0) value = 0;
        return value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
    }

    private static string M2(double squareFeet)
    {
        if (!double.IsFinite(squareFeet)) return "không đo được";
        var value = Math.Round(squareFeet * SquareMetresPerSquareFoot, 3);
        if (value == 0) value = 0;
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Percent(double share)
    {
        if (!double.IsFinite(share)) return "không đo được";
        return (share * 100).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Ratio(double value)
    {
        if (!double.IsFinite(value)) return "không đo được";
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
