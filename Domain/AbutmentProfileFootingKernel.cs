namespace BIM.DatViet.Domain;

/// <summary>
/// Builds a footing plan from three measured profile station sections.
/// It intentionally does not loft an intermediate solid.
/// </summary>
public static class AbutmentProfileFootingKernel
{
    public readonly record struct StationSection(
        long SourceElementIdValue,
        double Station,
        PlanPoint2 EdgeA,
        PlanPoint2 EdgeB,
        double BottomElevation,
        double TopElevation);

    public readonly record struct Resolution(
        IReadOnlyList<PlanPoint2> Boundary,
        double BottomElevation,
        double TopElevation,
        double CenterMismatch);

    public static bool TryResolve(
        IReadOnlyCollection<StationSection> sections,
        PlanPoint2 transverseDirection,
        double tolerance,
        out Resolution resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;
        if (sections.Count != 3 || transverseDirection.Length <= 1e-12 ||
            !double.IsFinite(tolerance) || tolerance <= 0)
        {
            error = "Profile footing requires exactly three sections and a valid transverse direction.";
            return false;
        }

        var ordered = sections.OrderBy(section => section.Station).ToArray();
        if (ordered[1].Station - ordered[0].Station <= tolerance ||
            ordered[2].Station - ordered[1].Station <= tolerance)
        {
            error = "Profile footing station coordinates are duplicated.";
            return false;
        }
        if (ordered.Any(section =>
                !double.IsFinite(section.BottomElevation) || !double.IsFinite(section.TopElevation) ||
                section.TopElevation <= section.BottomElevation + tolerance))
        {
            error = "Profile footing elevations are invalid.";
            return false;
        }

        var transverse = transverseDirection.Normalize();
        static (PlanPoint2 Low, PlanPoint2 High) Orient(
            StationSection section, PlanPoint2 axis) =>
            section.EdgeA.Dot(axis) <= section.EdgeB.Dot(axis)
                ? (section.EdgeA, section.EdgeB)
                : (section.EdgeB, section.EdgeA);
        var left = Orient(ordered[0], transverse);
        var center = Orient(ordered[1], transverse);
        var right = Orient(ordered[2], transverse);
        var fraction = (ordered[1].Station - ordered[0].Station) /
                       (ordered[2].Station - ordered[0].Station);
        var expectedLow = left.Low + (right.Low - left.Low) * fraction;
        var expectedHigh = left.High + (right.High - left.High) * fraction;
        var centerMismatch = Math.Max(
            (center.Low - expectedLow).Length,
            (center.High - expectedHigh).Length);
        if (centerMismatch > tolerance)
        {
            error = "Center profile does not interpolate between left and right station sections.";
            return false;
        }

        var bottomSpread = ordered.Max(section => section.BottomElevation) -
                           ordered.Min(section => section.BottomElevation);
        var topSpread = ordered.Max(section => section.TopElevation) -
                        ordered.Min(section => section.TopElevation);
        if (bottomSpread > tolerance || topSpread > tolerance)
        {
            error = "Footing elevations are inconsistent across profile stations.";
            return false;
        }

        var boundary = GeometryKernel.ConvexHull([left.Low, right.Low, right.High, left.High]);
        if (boundary.Count != 4 || !GeometryKernel.TryValidateParallelogram(
                boundary, 0.001, tolerance, out _, out _))
        {
            error = "Profile station sections do not define a rectangle/parallelogram footing.";
            return false;
        }

        resolution = new Resolution(
            boundary,
            ordered.Average(section => section.BottomElevation),
            ordered.Average(section => section.TopElevation),
            centerMismatch);
        return true;
    }
}
