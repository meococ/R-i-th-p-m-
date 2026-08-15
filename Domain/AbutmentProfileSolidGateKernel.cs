using System.Globalization;
using System.Text;

namespace BIM.DatViet.Domain;

/// <summary>
/// Splits the profile-to-solid check into two quantities that must never share a gate.
/// <para>
/// Plan (X/Y) mismatch stays fail-closed at the configured millimetre gate — that is a real
/// geometric disagreement about the path the bars will follow. Elevation / depth mismatch is
/// a drawing-versus-family thickness difference: the mats still sit on the measured solid
/// top and soffit, so the footing zone may lock. Widening the plan gate to swallow a depth
/// delta would hide a real sideways miss of the same size.
/// </para>
/// </summary>
public static class AbutmentProfileSolidGateKernel
{
    private const double MillimetresPerFoot = 304.8;

    /// <summary>
    /// Same epsilon as <see cref="AbutmentFootingBandKernel"/>: a difference that is exactly
    /// the declared gate has to read as "within gate".
    /// </summary>
    public const double ComparisonEpsilon = 1e-6 / MillimetresPerFoot;

    public enum Verdict
    {
        /// <summary>Plan and elevation both sit inside the plan gate.</summary>
        Match,

        /// <summary>
        /// Plan is inside the gate; only thickness / elevation disagrees. The footing zone
        /// may lock. Stem, backwall and wing are not decided here.
        /// </summary>
        DepthMismatch,

        /// <summary>Plan (X/Y) exceeds the gate. Fail-closed; do not lock the footing zone.</summary>
        PlanMismatch
    }

    public readonly record struct Measurement(
        double PlanMismatch,
        double BottomElevationMismatch,
        double TopElevationMismatch,
        double ElevationMismatch,
        double ProfileBottom,
        double ProfileTop,
        double? SolidBottom,
        double? SolidTop,
        double PlanTolerance,
        double FarthestPlanPointX,
        double FarthestPlanPointY);

    public readonly record struct PlacementElevations(double Bottom, double Top);

    /// <summary>
    /// Decides the gate from already-measured distances. <paramref name="planTolerance"/> is
    /// the plan-only gate; elevation is compared against the same number only to classify
    /// "worth reporting", never to widen the plan gate.
    /// </summary>
    public static Verdict Evaluate(
        double planMismatch,
        double elevationMismatch,
        double planTolerance)
    {
        if (!double.IsFinite(planTolerance) || planTolerance <= 0)
            return Verdict.PlanMismatch;
        if (!double.IsFinite(planMismatch) || planMismatch > planTolerance + ComparisonEpsilon)
            return Verdict.PlanMismatch;
        if (!double.IsFinite(elevationMismatch) || elevationMismatch > planTolerance + ComparisonEpsilon)
            return Verdict.DepthMismatch;
        return Verdict.Match;
    }

    public static bool AllowsFootingZoneLock(Verdict verdict) =>
        verdict is Verdict.Match or Verdict.DepthMismatch;

    /// <summary>
    /// Bars sit on the measured solid faces when both are known. Profile elevations stay in
    /// the receipt so a 2000 mm drawing versus an 1800 mm family is visible, not silent.
    /// </summary>
    public static PlacementElevations ResolvePlacementElevations(
        double profileBottom,
        double profileTop,
        double? solidBottom,
        double? solidTop)
    {
        if (solidBottom is { } soffit &&
            solidTop is { } top &&
            double.IsFinite(soffit) &&
            double.IsFinite(top) &&
            top > soffit + ComparisonEpsilon)
            return new PlacementElevations(soffit, top);
        return new PlacementElevations(profileBottom, profileTop);
    }

    public static Measurement Measure(
        IReadOnlyList<PlanPoint2> profileBoundary,
        IReadOnlyList<PlanPoint2> solidBoundary,
        double profileBottom,
        double profileTop,
        double? solidBottom,
        double? solidTop,
        double planTolerance)
    {
        var planMismatch = SymmetricMaximumBoundaryDistance(
            profileBoundary, solidBoundary, out var farthest, out _);
        var bottomMismatch = solidBottom is { } soffit && double.IsFinite(soffit)
            ? Math.Abs(profileBottom - soffit)
            : 0;
        var topMismatch = solidTop is { } top && double.IsFinite(top)
            ? Math.Abs(profileTop - top)
            : 0;
        return new Measurement(
            PlanMismatch: planMismatch,
            BottomElevationMismatch: bottomMismatch,
            TopElevationMismatch: topMismatch,
            ElevationMismatch: Math.Max(bottomMismatch, topMismatch),
            ProfileBottom: profileBottom,
            ProfileTop: profileTop,
            SolidBottom: solidBottom,
            SolidTop: solidTop,
            PlanTolerance: planTolerance,
            FarthestPlanPointX: farthest.X,
            FarthestPlanPointY: farthest.Y);
    }

    public static double SymmetricMaximumBoundaryDistance(
        IReadOnlyList<PlanPoint2> first,
        IReadOnlyList<PlanPoint2> second,
        out PlanPoint2 farthestPoint,
        out double farthestDistance)
    {
        farthestPoint = default;
        farthestDistance = double.PositiveInfinity;
        if (first.Count < 3 || second.Count < 3) return double.PositiveInfinity;

        var firstWay = MaximumVertexToPolygon(first, second);
        var secondWay = MaximumVertexToPolygon(second, first);
        if (firstWay.Distance >= secondWay.Distance)
        {
            farthestPoint = firstWay.Point;
            farthestDistance = firstWay.Distance;
        }
        else
        {
            farthestPoint = secondWay.Point;
            farthestDistance = secondWay.Distance;
        }
        return Math.Max(firstWay.Distance, secondWay.Distance);
    }

    public static string DescribePlanMismatch(Measurement measurement)
    {
        var message = new StringBuilder();
        message.Append("Profile-to-solid lệch MẶT BẰNG ")
            .Append(Mm(measurement.PlanMismatch))
            .Append(" mm vượt gate ")
            .Append(Mm(measurement.PlanTolerance))
            .Append(" mm (đỉnh lệch xa nhất tại local Y/X = ")
            .Append(Mm(measurement.FarthestPlanPointY))
            .Append(" / ")
            .Append(Mm(measurement.FarthestPlanPointX))
            .Append(" mm). ");
        message.Append("Đây là lệch ngang của biên PROFILE so với biên solid — fail-closed, không khóa zone móng. ");
        message.Append("Lệch cao độ ")
            .Append(Mm(measurement.ElevationMismatch))
            .Append(" mm không được cộng vào gate mặt bằng và không được dùng để nới gate.");
        return message.ToString();
    }

    public static string DescribeDepthMismatch(Measurement measurement)
    {
        var message = new StringBuilder();
        message.Append("PROFILE móng và solid lệch CHIỀU DÀY ")
            .Append(Mm(measurement.ElevationMismatch))
            .Append(" mm (PROFILE đáy ")
            .Append(Mm(measurement.ProfileBottom))
            .Append(" → đỉnh ")
            .Append(Mm(measurement.ProfileTop))
            .Append(" = ")
            .Append(Mm(measurement.ProfileTop - measurement.ProfileBottom))
            .Append(" mm");
        if (measurement.SolidBottom is { } soffit && measurement.SolidTop is { } top)
        {
            message.Append("; solid đáy ")
                .Append(Mm(soffit))
                .Append(" → đỉnh ")
                .Append(Mm(top))
                .Append(" = ")
                .Append(Mm(top - soffit))
                .Append(" mm");
        }
        message.Append("). Lệch mặt bằng ")
            .Append(Mm(measurement.PlanMismatch))
            .Append(" mm, trong gate ")
            .Append(Mm(measurement.PlanTolerance))
            .Append(" mm. Không khóa 4 lớp móng: path/cover bám biên PROFILE và mặt top/bottom solid đã đo. ")
            .Append("Không im lặng thay PROFILE bằng solid; lệch dày được ghi ra, không abort zone móng.");
        return message.ToString();
    }

    public static string DescribeMatch(Measurement measurement) =>
        "Đã dựng footing profile-first từ L/Center/R; " +
        $"lệch mặt bằng={Mm(measurement.PlanMismatch)} mm, " +
        $"lệch cao độ={Mm(measurement.ElevationMismatch)} mm, " +
        $"gate mặt bằng {Mm(measurement.PlanTolerance)} mm.";

    private static (PlanPoint2 Point, double Distance) MaximumVertexToPolygon(
        IReadOnlyList<PlanPoint2> source,
        IReadOnlyList<PlanPoint2> target)
    {
        var farthest = source[0];
        var distance = double.NegativeInfinity;
        foreach (var point in source)
        {
            var nearest = Enumerable.Range(0, target.Count)
                .Select(index => GeometryKernel.DistancePointToSegment(
                    point, target[index], target[(index + 1) % target.Count]))
                .Min();
            if (nearest > distance)
            {
                distance = nearest;
                farthest = point;
            }
        }
        return (farthest, distance);
    }

    private static string Mm(double feet)
    {
        if (!double.IsFinite(feet)) return "không đo được";
        var value = Math.Round(feet * MillimetresPerFoot, 1);
        if (value == 0) value = 0;
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
