namespace BIM.DatViet.Domain;

public readonly record struct CenterlinePoint3(double X, double Y, double Z)
{
    public static CenterlinePoint3 operator -(CenterlinePoint3 left, CenterlinePoint3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double Dot(CenterlinePoint3 other) => X * other.X + Y * other.Y + Z * other.Z;
    public CenterlinePoint3 Scale(double value) => new(X * value, Y * value, Z * value);
}

public readonly record struct StraightCenterlineReadbackResult(
    bool Accepted,
    bool ActualWasReversed,
    double PlannedLength,
    double StartAxialDelta,
    double EndAxialDelta,
    double StartLateralOffset,
    double EndLateralOffset,
    double StartPointDistance,
    double EndPointDistance)
{
    public double StartShortening => Math.Max(0, StartAxialDelta);
    public double EndShortening => Math.Max(0, -EndAxialDelta);
    public double StartExtension => Math.Max(0, -StartAxialDelta);
    public double EndExtension => Math.Max(0, EndAxialDelta);
    public double MaximumLateralOffset => Math.Max(StartLateralOffset, EndLateralOffset);
    public double MaximumPointDistance => Math.Max(StartPointDistance, EndPointDistance);
}

public static class AbutmentCenterlineReadbackKernel
{
    public static StraightCenterlineReadbackResult CompareStraightBar(
        CenterlinePoint3 plannedStart,
        CenterlinePoint3 plannedEnd,
        CenterlinePoint3 actualStart,
        CenterlinePoint3 actualEnd,
        double lateralTolerance,
        double axialShorteningTolerance,
        double axialExtensionTolerance)
    {
        if (!IsFinite(plannedStart) || !IsFinite(plannedEnd) ||
            !IsFinite(actualStart) || !IsFinite(actualEnd) ||
            !double.IsFinite(lateralTolerance) || lateralTolerance < 0 ||
            !double.IsFinite(axialShorteningTolerance) || axialShorteningTolerance < 0 ||
            !double.IsFinite(axialExtensionTolerance) || axialExtensionTolerance < 0)
            return Invalid();

        var plannedVector = plannedEnd - plannedStart;
        var plannedLength = plannedVector.Length;
        if (!double.IsFinite(plannedLength) || plannedLength <= 1e-12)
            return Invalid();

        var direction = plannedVector.Scale(1 / plannedLength);
        var forward = Evaluate(
            actualStart, actualEnd, actualWasReversed: false,
            plannedStart, plannedEnd, direction, plannedLength,
            lateralTolerance, axialShorteningTolerance, axialExtensionTolerance);
        var reverse = Evaluate(
            actualEnd, actualStart, actualWasReversed: true,
            plannedStart, plannedEnd, direction, plannedLength,
            lateralTolerance, axialShorteningTolerance, axialExtensionTolerance);

        if (forward.Accepted != reverse.Accepted)
            return forward.Accepted ? forward : reverse;
        return Score(forward, lateralTolerance, axialShorteningTolerance, axialExtensionTolerance) <=
               Score(reverse, lateralTolerance, axialShorteningTolerance, axialExtensionTolerance)
            ? forward
            : reverse;
    }

    private static StraightCenterlineReadbackResult Evaluate(
        CenterlinePoint3 actualStart,
        CenterlinePoint3 actualEnd,
        bool actualWasReversed,
        CenterlinePoint3 plannedStart,
        CenterlinePoint3 plannedEnd,
        CenterlinePoint3 direction,
        double plannedLength,
        double lateralTolerance,
        double shorteningTolerance,
        double extensionTolerance)
    {
        var startOffset = actualStart - plannedStart;
        var endOffset = actualEnd - plannedEnd;
        var startAxial = startOffset.Dot(direction);
        var endAxial = endOffset.Dot(direction);
        var startLateral = (startOffset - direction.Scale(startAxial)).Length;
        var endLateral = (endOffset - direction.Scale(endAxial)).Length;
        var accepted =
            startLateral <= lateralTolerance + 1e-12 &&
            endLateral <= lateralTolerance + 1e-12 &&
            startAxial >= -extensionTolerance - 1e-12 &&
            startAxial <= shorteningTolerance + 1e-12 &&
            endAxial <= extensionTolerance + 1e-12 &&
            endAxial >= -shorteningTolerance - 1e-12 &&
            (actualEnd - actualStart).Dot(direction) > 1e-12;

        return new StraightCenterlineReadbackResult(
            accepted,
            actualWasReversed,
            plannedLength,
            startAxial,
            endAxial,
            startLateral,
            endLateral,
            startOffset.Length,
            endOffset.Length);
    }

    private static double Score(
        StraightCenterlineReadbackResult value,
        double lateralTolerance,
        double shorteningTolerance,
        double extensionTolerance)
    {
        static double Excess(double actual, double allowed) => Math.Max(0, actual - allowed);
        return
            Excess(value.StartLateralOffset, lateralTolerance) +
            Excess(value.EndLateralOffset, lateralTolerance) +
            Excess(value.StartShortening, shorteningTolerance) +
            Excess(value.EndShortening, shorteningTolerance) +
            Excess(value.StartExtension, extensionTolerance) +
            Excess(value.EndExtension, extensionTolerance) +
            value.StartPointDistance * 1e-6 +
            value.EndPointDistance * 1e-6;
    }

    private static bool IsFinite(CenterlinePoint3 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static StraightCenterlineReadbackResult Invalid() =>
        new(false, false, 0,
            double.PositiveInfinity, double.PositiveInfinity,
            double.PositiveInfinity, double.PositiveInfinity,
            double.PositiveInfinity, double.PositiveInfinity);
}
