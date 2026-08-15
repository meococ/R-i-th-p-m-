namespace BIM.DatViet.Domain;

/// <summary>
/// Anchors an explicit station chain to a semantic concrete edge. Reading an asymmetric chain
/// from the opposite edge mirrors every bar while the bar count and the plan-versus-readback
/// comparison both stay clean, so the datum edge must come from declared semantics resolved
/// against measured geometry, never from the sign of a host frame axis.
/// All lengths passed to this kernel must use one consistent unit.
/// </summary>
public static class AbutmentStationDatumKernel
{
    /// <summary>
    /// Accepted difference in millimeters between a measured stem shoulder and the declared
    /// toe/heel width. Matches the footing dimension gate already used during recognition.
    /// </summary>
    public const double ShoulderToleranceMm = 25;

    /// <summary>Which end of the semantic axis carries the resolved edge.</summary>
    public enum DatumSide
    {
        AxisMinimum,
        AxisMaximum
    }

    /// <summary>
    /// Footing cantilever widths of one validated station section, measured along the semantic
    /// axis from each footing edge to the nearest stem face.
    /// </summary>
    public readonly record struct StemShoulder(double AxisMinimumWidth, double AxisMaximumWidth);

    /// <summary>
    /// Reports whether reading <paramref name="offsets"/> from the opposite edge of a
    /// <paramref name="span"/> wide concrete band reproduces the same chain. Invalid input is
    /// reported as asymmetric so the caller keeps requiring an explicit datum edge.
    /// </summary>
    public static bool IsMirrorSymmetric(
        IReadOnlyList<double> offsets,
        double span,
        double tolerance,
        out double maximumMirrorShift)
    {
        maximumMirrorShift = double.PositiveInfinity;
        if (offsets is null || offsets.Count == 0 ||
            !double.IsFinite(span) || span <= 0 ||
            !double.IsFinite(tolerance) || tolerance < 0 ||
            offsets.Any(offset => !double.IsFinite(offset) || offset < 0 || offset > span) ||
            offsets.Zip(offsets.Skip(1), (first, second) => second - first).Any(delta => delta <= 0))
            return false;
        maximumMirrorShift = Enumerable.Range(0, offsets.Count)
            .Max(index => Math.Abs(span - offsets[offsets.Count - 1 - index] - offsets[index]));
        return maximumMirrorShift <= tolerance;
    }

    /// <summary>
    /// Decides which end of the semantic axis carries the toe cantilever by matching the
    /// measured stem shoulders against the declared toe/heel widths. The declared widths only
    /// label the two measured shoulders; they never supply a position and they never have to equal
    /// the measurement.
    /// <para>
    /// The question asked is which of the two pairings fits better, not whether either width is
    /// reproduced. Requiring the measurement to land within a tolerance of the declaration would
    /// lock every abutment whose cantilevers differ from the one the profile was written for while
    /// answering nothing extra, because a label carries no position — only an ordering.
    /// </para>
    /// <para>
    /// That ordering is read directly: the wider shoulder takes the wider declaration. Summed
    /// absolute error, which this kernel used until 2026-08-15, is not a safe stand-in for it.
    /// Writing f(x) = |x−toe| − |x−heel|, the two pairings differ by f(minimumWidth) − f(maximumWidth),
    /// and f is a ramp that saturates outside [toe, heel]; the separation therefore equals twice the
    /// overlap between the measured span and the declared span, and collapses to exactly zero the
    /// moment those two intervals stop overlapping. MỐ CẦU VẠN CỦI measured 2100/2300 against a
    /// declared 2317/2538 and tied at 455 mm to the millimetre while the shoulders differed by
    /// 200 mm — eight times the tolerance. The blindness is structural, so no tolerance repairs it.
    /// </para>
    /// <para>
    /// Ordering fails only where there is no ordering to read, and both of those cases are already
    /// gated above: declared widths too close to separate, or measured shoulders too close to
    /// separate. Past those gates the labelling is total.
    /// </para>
    /// <paramref name="maximumDeviation"/> reports how far the winning pairing actually sits from
    /// the declaration so the caller can warn that this abutment is not the drawn one.
    /// </summary>
    public static bool TryResolveToeSide(
        IReadOnlyList<StemShoulder> shoulders,
        double declaredToeWidth,
        double declaredHeelWidth,
        double tolerance,
        out DatumSide toeSide,
        out double maximumDeviation,
        out string error)
    {
        toeSide = DatumSide.AxisMinimum;
        maximumDeviation = double.NaN;
        error = string.Empty;
        if (!double.IsFinite(tolerance) || tolerance <= 0 ||
            shoulders is null || shoulders.Count == 0 ||
            shoulders.Any(item =>
                !double.IsFinite(item.AxisMinimumWidth) || item.AxisMinimumWidth <= 0 ||
                !double.IsFinite(item.AxisMaximumWidth) || item.AxisMaximumWidth <= 0))
        {
            error = "ABUTMENT_STATION_DATUM_MEASUREMENT_INVALID: " +
                    "không đo được hai vai bệ hai bên thân mố từ section PROFILE.";
            return false;
        }
        if (!double.IsFinite(declaredToeWidth) || declaredToeWidth <= 0 ||
            !double.IsFinite(declaredHeelWidth) || declaredHeelWidth <= 0)
        {
            error = "ABUTMENT_STATION_DATUM_TOE_HEEL_UNDECLARED: " +
                    "profile chưa khai toeWidthMm/heelWidthMm nên không gắn được nhãn mũi/gót cho hai cạnh bệ.";
            return false;
        }
        if (Math.Abs(declaredToeWidth - declaredHeelWidth) <= tolerance)
        {
            error = $"ABUTMENT_STATION_DATUM_TOE_HEEL_INDISTINGUISHABLE: " +
                    $"toeWidthMm={declaredToeWidth:0.#} và heelWidthMm={declaredHeelWidth:0.#} " +
                    $"chênh nhau không quá {tolerance:0.#} mm nên không phân biệt được hai cạnh bệ.";
            return false;
        }

        var declaredToeIsWider = declaredToeWidth > declaredHeelWidth;
        DatumSide? resolved = null;
        var deviation = 0d;
        foreach (var shoulder in shoulders)
        {
            if (Math.Abs(shoulder.AxisMinimumWidth - shoulder.AxisMaximumWidth) <= tolerance)
            {
                error = $"ABUTMENT_STATION_DATUM_EDGE_AMBIGUOUS: hai vai bệ đo được " +
                        $"{shoulder.AxisMinimumWidth:0.#} mm và {shoulder.AxisMaximumWidth:0.#} mm " +
                        $"chênh nhau không quá {tolerance:0.#} mm, nên hình học không có bằng chứng nào " +
                        "phân biệt cạnh mũi với cạnh gót.";
                return false;
            }
            var side = declaredToeIsWider == (shoulder.AxisMinimumWidth > shoulder.AxisMaximumWidth)
                ? DatumSide.AxisMinimum
                : DatumSide.AxisMaximum;
            deviation = Math.Max(deviation, side == DatumSide.AxisMinimum
                ? Math.Max(Math.Abs(shoulder.AxisMinimumWidth - declaredToeWidth),
                    Math.Abs(shoulder.AxisMaximumWidth - declaredHeelWidth))
                : Math.Max(Math.Abs(shoulder.AxisMaximumWidth - declaredToeWidth),
                    Math.Abs(shoulder.AxisMinimumWidth - declaredHeelWidth)));
            if (resolved is null) resolved = side;
            else if (resolved != side)
            {
                error = "ABUTMENT_STATION_DATUM_SECTIONS_INCONSISTENT: " +
                        "các section PROFILE không cho cùng một kết luận về phía mũi bệ.";
                return false;
            }
        }

        toeSide = resolved!.Value;
        maximumDeviation = deviation;
        return true;
    }

    /// <summary>
    /// Message for a labelling that resolved but sits far from the declared widths. The labels held,
    /// so the chain is anchored correctly; the profile simply describes another abutment.
    /// </summary>
    public static string DescribeShoulderDeviation(
        double maximumDeviation, double declaredToeWidth, double declaredHeelWidth, double tolerance) =>
        $"Vai bệ đo được lệch tới {maximumDeviation:0.#} mm so với toeWidthMm={declaredToeWidth:0.#} / " +
        $"heelWidthMm={declaredHeelWidth:0.#} (dung sai đối chiếu {tolerance:0.#} mm). " +
        "Nhãn mũi/gót vẫn xác định được vì chỉ cần thứ tự hai vai, và chuỗi ga vẫn neo từ cạnh bê tông " +
        "ĐO ĐƯỢC. Nhưng mố đang mở không đúng kích thước preset khai — hãy cập nhật preset.";
}
