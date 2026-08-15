using Autodesk.Revit.DB;

namespace BIM.DatViet.Recognition;

internal static class GeometryService
{
    private const double Epsilon = 1e-9;

    public static IReadOnlyList<Solid> GetSolids(Element element)
    {
        var options = new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = true,
            ComputeReferences = true
        };

        var solids = new List<Solid>();
        var geometry = element.get_Geometry(options);
        if (geometry is not null) Collect(geometry, Transform.Identity, solids);
        return solids;
    }

    private static void Collect(GeometryElement geometry, Transform transform, ICollection<Solid> solids)
    {
        foreach (var geometryObject in geometry)
        {
            // IncludeNonVisibleObjects is on so that nested model lines stay reachable; without this
            // guard the geometry an author switched off would count as concrete.
            if (geometryObject.Visibility != Visibility.Visible) continue;
            switch (geometryObject)
            {
                case Solid solid when solid.Volume > Epsilon && solid.Faces.Size > 0:
                    solids.Add(transform.IsIdentity ? solid : SolidUtils.CreateTransformed(solid, transform));
                    break;
                case GeometryInstance instance:
                    Collect(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), solids);
                    break;
            }
        }
    }

    public static IReadOnlyList<XYZ> GetVertices(IEnumerable<Solid> solids)
    {
        var vertices = new List<XYZ>();
        foreach (var solid in solids)
        {
            foreach (Edge edge in solid.Edges)
            {
                var points = edge.Tessellate();
                if (points.Count == 0) continue;
                vertices.Add(points[0]);
                vertices.Add(points[^1]);
            }
        }

        return vertices;
    }

    public static BoundingBoxXYZ? GetBounds(Element element)
    {
        var solids = GetSolids(element);
        var points = GetVertices(solids);
        if (points.Count == 0) return element.get_BoundingBox(null);

        var min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        return new BoundingBoxXYZ { Min = min, Max = max };
    }

    public static bool Intersects(BoundingBoxXYZ? first, BoundingBoxXYZ? second, double tolerance)
    {
        if (first is null || second is null) return false;
        return first.Min.X <= second.Max.X + tolerance && first.Max.X >= second.Min.X - tolerance &&
               first.Min.Y <= second.Max.Y + tolerance && first.Max.Y >= second.Min.Y - tolerance &&
               first.Min.Z <= second.Max.Z + tolerance && first.Max.Z >= second.Min.Z - tolerance;
    }

    public static IReadOnlyList<double> UniqueCoordinates(IEnumerable<double> values, double tolerance)
    {
        var ordered = values.OrderBy(value => value).ToList();
        var result = new List<double>();
        foreach (var value in ordered)
        {
            if (result.Count == 0 || Math.Abs(value - result[^1]) > tolerance) result.Add(value);
        }

        return result;
    }

    public static bool IsParallel(XYZ first, XYZ second, double angularToleranceRadians)
    {
        var dot = Math.Abs(first.Normalize().DotProduct(second.Normalize()));
        return dot >= Math.Cos(angularToleranceRadians);
    }
}
