using Autodesk.Revit.DB;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

internal interface IAbutmentFootingBoundaryStrategy
{
    AbutmentFootingBoundary? Resolve(IReadOnlyList<XYZ> worldPoints, AbutmentLocalFrame frame);
}

internal static class AbutmentFootingBoundaryResolver
{
    private static readonly IAbutmentFootingBoundaryStrategy Parallelogram =
        new ParallelogramFootingBoundaryStrategy();
    private static readonly IAbutmentFootingBoundaryStrategy ConvexHullClippedToSolid =
        new ConvexHullClippedToSolidBoundaryStrategy();

    public static AbutmentFootingBoundary? Resolve(
        IReadOnlyList<XYZ> worldPoints,
        AbutmentLocalFrame frame,
        AbutmentFootingBoundaryStrategy strategy) => strategy switch
        {
            AbutmentFootingBoundaryStrategy.Parallelogram => Parallelogram.Resolve(worldPoints, frame),
            AbutmentFootingBoundaryStrategy.ConvexHullClippedToSolid =>
                ConvexHullClippedToSolid.Resolve(worldPoints, frame),
            _ => null
        };

    internal static bool TryGetBottomHull(
        IReadOnlyList<XYZ> worldPoints,
        AbutmentLocalFrame frame,
        out IReadOnlyList<PlanPoint2> hull,
        out double bottomWorldZ)
    {
        hull = [];
        bottomWorldZ = 0;
        if (worldPoints.Count < 4) return false;
        var minLocalZ = worldPoints.Min(point => frame.ToLocal(point).Z);
        var zTolerance = UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters);
        var bottom = worldPoints
            .Where(point => Math.Abs(frame.ToLocal(point).Z - minLocalZ) <= zTolerance)
            .ToList();
        if (bottom.Count < 4) return false;
        hull = GeometryKernel.ConvexHull(bottom.Select(point => new PlanPoint2(point.X, point.Y)).ToList());
        bottomWorldZ = bottom.Average(point => point.Z);
        return hull.Count >= 3;
    }

    internal static XYZ Orient(XYZ direction, XYZ hint) =>
        direction.DotProduct(hint) < 0 ? -direction : direction;
}

internal sealed class ParallelogramFootingBoundaryStrategy : IAbutmentFootingBoundaryStrategy
{
    public AbutmentFootingBoundary? Resolve(IReadOnlyList<XYZ> worldPoints, AbutmentLocalFrame frame)
    {
        if (!AbutmentFootingBoundaryResolver.TryGetBottomHull(
                worldPoints, frame, out var hull, out var bottomWorldZ) ||
            hull.Count != 4 ||
            !GeometryKernel.TryValidateParallelogram(
                hull, 0.001, UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters),
                out var longEdgeLength, out var endEdgeLength) ||
            !GeometryKernel.TryFindPrimaryEdgeDirections(
                hull, out var longitudinal2, out var transverse2, out var acuteAngleDeg))
            return null;

        var longitudinal = AbutmentFootingBoundaryResolver.Orient(
            new XYZ(longitudinal2.X, longitudinal2.Y, 0).Normalize(), frame.Longitudinal);
        var transverse = AbutmentFootingBoundaryResolver.Orient(
            new XYZ(transverse2.X, transverse2.Y, 0).Normalize(), frame.Transverse);
        return new AbutmentFootingBoundary
        {
            Vertices = hull.Select(point => new XYZ(point.X, point.Y, bottomWorldZ)).ToList(),
            LongitudinalDirection = longitudinal,
            TransverseDirection = transverse,
            AcuteAngleDeg = acuteAngleDeg,
            LongEdgeLength = longEdgeLength,
            EndEdgeLength = endEdgeLength
        };
    }
}

internal sealed class ConvexHullClippedToSolidBoundaryStrategy : IAbutmentFootingBoundaryStrategy
{
    public AbutmentFootingBoundary? Resolve(IReadOnlyList<XYZ> worldPoints, AbutmentLocalFrame frame)
    {
        if (!AbutmentFootingBoundaryResolver.TryGetBottomHull(
                worldPoints, frame, out var hull, out var bottomWorldZ))
            return null;

        var longitudinal = new XYZ(frame.Longitudinal.X, frame.Longitudinal.Y, 0);
        var transverse = new XYZ(frame.Transverse.X, frame.Transverse.Y, 0);
        if (longitudinal.GetLength() < 0.9 || transverse.GetLength() < 0.9) return null;
        longitudinal = longitudinal.Normalize();
        transverse = transverse.Normalize();
        var vertices = hull.Select(point => new XYZ(point.X, point.Y, bottomWorldZ)).ToList();
        var longitudinalCoordinates = vertices.Select(point => point.DotProduct(longitudinal)).ToList();
        var transverseCoordinates = vertices.Select(point => point.DotProduct(transverse)).ToList();
        var angle = Math.Acos(Math.Clamp(
            Math.Abs(longitudinal.DotProduct(transverse)), 0, 1)) * 180 / Math.PI;
        return new AbutmentFootingBoundary
        {
            Vertices = vertices,
            LongitudinalDirection = longitudinal,
            TransverseDirection = transverse,
            AcuteAngleDeg = angle,
            LongEdgeLength = longitudinalCoordinates.Max() - longitudinalCoordinates.Min(),
            EndEdgeLength = transverseCoordinates.Max() - transverseCoordinates.Min()
        };
    }
}
