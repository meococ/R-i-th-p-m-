using Autodesk.Revit.DB;
using BIM.DatViet.Models;
using BIM.DatViet.Domain;

namespace BIM.DatViet.Recognition;

public sealed class OpeningDetector
{
    private readonly Document _document;

    public OpeningDetector(Document document)
    {
        _document = document;
    }

    public IReadOnlyList<OpeningEnvelope> Detect(ManholeAssembly assembly, double expansion)
    {
        var openings = new List<OpeningEnvelope>();
        foreach (var wall in assembly.Walls)
        {
            DetectFaceLoops(wall, expansion, openings);
            DetectPipes(wall, expansion, openings);
            DetectSleeves(wall, expansion, openings);
        }

        DetectSlabLoops(assembly, assembly.Bottom, expansion, openings);
        DetectSlabLoops(assembly, assembly.Top, expansion, openings);
        DetectSlabPenetrations(assembly, assembly.Bottom, expansion, openings);
        DetectSlabPenetrations(assembly, assembly.Top, expansion, openings);

        return Deduplicate(openings);
    }

    private void DetectSlabLoops(ManholeAssembly assembly, ManholeComponent component, double expansion,
        ICollection<OpeningEnvelope> openings)
    {
        var host = _document.GetElement(component.HostId);
        if (host is null) return;
        foreach (var solid in GeometryService.GetSolids(host))
        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace planar ||
                !GeometryService.IsParallel(planar.FaceNormal, XYZ.BasisZ, Math.PI / 180)) continue;
            var loops = planar.GetEdgesAsCurveLoops();
            if (loops.Count < 2) continue;
            var projected = loops.Select(loop => ProjectSlabLoop(loop, assembly)).Where(value => value is not null)
                .Cast<SlabLoopBounds>().ToList();
            if (projected.Count < 2) continue;
            var outer = projected.OrderByDescending(value => value.Area).First();
            foreach (var inner in projected.Where(value => !ReferenceEquals(value, outer)))
                AddSlabOpening(openings, component, ElementId.InvalidElementId,
                    inner.MinU - expansion, inner.MaxU + expansion, inner.MinV - expansion,
                    inner.MaxV + expansion, false, "VOID");
        }
    }

    private void DetectSlabPenetrations(ManholeAssembly assembly, ManholeComponent component, double expansion,
        ICollection<OpeningEnvelope> openings)
    {
        var bounds = component.BoundingBox;
        if (bounds is null) return;
        var categories = new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_GenericModel };
        var filter = new ElementMulticategoryFilter(categories.Select(value => new ElementId(value)).ToList());
        foreach (var element in new FilteredElementCollector(_document).WherePasses(filter).WhereElementIsNotElementType())
        {
            var elementBounds = GeometryService.GetBounds(element);
            if (elementBounds is null || elementBounds.Max.Z < bounds.Min.Z - expansion ||
                elementBounds.Min.Z > bounds.Max.Z + expansion) continue;
            var corners = BoxCorners(elementBounds).ToList();
            var minU = corners.Min(assembly.Frame.ToU) - expansion;
            var maxU = corners.Max(assembly.Frame.ToU) + expansion;
            var minV = corners.Min(assembly.Frame.ToV) - expansion;
            var maxV = corners.Max(assembly.Frame.ToV) + expansion;
            if (maxU < component.MinU || minU > component.MaxU ||
                maxV < component.MinV || minV > component.MaxV) continue;
            var searchable = element is FamilyInstance family ? $"{family.Name} {family.Symbol?.FamilyName}" : string.Empty;
            if (element.Category?.Id != new ElementId(BuiltInCategory.OST_GenericModel) ||
                ContainsAny(searchable, "SLEEVE", "OPENING", "LỖ", "LO ONG", "Ổ ỐNG"))
                AddSlabOpening(openings, component, element.Id, minU, maxU, minV, maxV, true, "PENETRATION");
        }
    }

    private void DetectFaceLoops(WallDescriptor wall, double expansion, ICollection<OpeningEnvelope> openings)
    {
        var host = _document.GetElement(wall.HostId);
        if (host is null) return;
        foreach (var solid in GeometryService.GetSolids(host))
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planarFace ||
                    !GeometryService.IsParallel(planarFace.FaceNormal, wall.Outward, Math.PI / 180)) continue;
                var faceDistance = Math.Abs((planarFace.Origin - wall.Center).DotProduct(wall.Outward));
                if (Math.Abs(faceDistance - wall.Thickness / 2) > UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters)) continue;

                var loops = planarFace.GetEdgesAsCurveLoops();
                if (loops.Count < 2) continue;
                var bounds = loops.Select(loop => ProjectLoop(loop, wall)).Where(bound => bound is not null).Cast<LoopBounds>().ToList();
                if (bounds.Count < 2) continue;
                var outer = bounds.OrderByDescending(bound => bound.Area).First();
                foreach (var inner in bounds.Where(bound => !ReferenceEquals(bound, outer)))
                {
                    AddOpening(openings, wall, ElementId.InvalidElementId, inner.MinAlong - expansion,
                        inner.MaxAlong + expansion, inner.MinZ - expansion, inner.MaxZ + expansion, false, "VOID");
                }
            }
        }
    }

    private void DetectPipes(WallDescriptor wall, double expansion, ICollection<OpeningEnvelope> openings)
    {
        var pipeCategories = new[]
        {
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory
        };
        var filter = new ElementMulticategoryFilter(pipeCategories.Select(category => new ElementId(category)).ToList());
        foreach (var element in new FilteredElementCollector(_document).WherePasses(filter).WhereElementIsNotElementType())
        {
            if (element.Location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);
                var d0 = (start - wall.Center).DotProduct(wall.Outward);
                var d1 = (end - wall.Center).DotProduct(wall.Outward);
                if (Math.Sign(d0) == Math.Sign(d1) && Math.Min(Math.Abs(d0), Math.Abs(d1)) > wall.Thickness / 2 + expansion) continue;
                var denominator = d0 - d1;
                if (Math.Abs(denominator) < 1e-9) continue;
                var parameter = d0 / denominator;
                if (parameter < 0 || parameter > 1) continue;
                var center = start + (end - start) * parameter;
                var diameter = GetDiameter(element);
                if (diameter <= 0) diameter = Math.Max(expansion * 2, UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters));
                var direction = (end - start).Normalize();
                var radius = GeometryKernel.PipeEnvelopeRadius(diameter, direction.DotProduct(wall.Outward), expansion);
                AddOpening(openings, wall, element.Id, center.DotProduct(wall.Tangent) - radius,
                    center.DotProduct(wall.Tangent) + radius, center.Z - radius, center.Z + radius, true, "PIPE");
            }
            else
            {
                AddBoundingBoxOpening(element, wall, expansion, openings, "PIPE-FITTING");
            }
        }
    }

    private void DetectSleeves(WallDescriptor wall, double expansion, ICollection<OpeningEnvelope> openings)
    {
        foreach (var family in new FilteredElementCollector(_document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            var searchable = $"{family.Name} {family.Symbol?.FamilyName}";
            if (!ContainsAny(searchable, "SLEEVE", "OPENING", "LỖ", "LO ONG", "Ổ ỐNG")) continue;
            AddBoundingBoxOpening(family, wall, expansion, openings, "SLEEVE");
        }
    }

    private void AddBoundingBoxOpening(Element element, WallDescriptor wall, double expansion,
        ICollection<OpeningEnvelope> openings, string prefix)
    {
        var bounds = GeometryService.GetBounds(element);
        if (bounds is null) return;
        var corners = BoxCorners(bounds).ToList();
        var distances = corners.Select(point => (point - wall.Center).DotProduct(wall.Outward)).ToList();
        if (distances.Min() > wall.Thickness / 2 + expansion || distances.Max() < -wall.Thickness / 2 - expansion) return;
        AddOpening(openings, wall, element.Id,
            corners.Min(point => point.DotProduct(wall.Tangent)) - expansion,
            corners.Max(point => point.DotProduct(wall.Tangent)) + expansion,
            bounds.Min.Z - expansion, bounds.Max.Z + expansion, true, prefix);
    }

    private static double GetDiameter(Element element)
    {
        var parameter = element.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER) ??
                        element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        return parameter?.StorageType == StorageType.Double ? parameter.AsDouble() : 0;
    }

    private static LoopBounds? ProjectLoop(CurveLoop loop, WallDescriptor wall)
    {
        var points = loop.SelectMany(curve => curve.Tessellate()).ToList();
        if (points.Count < 3) return null;
        return new LoopBounds
        {
            MinAlong = points.Min(point => point.DotProduct(wall.Tangent)),
            MaxAlong = points.Max(point => point.DotProduct(wall.Tangent)),
            MinZ = points.Min(point => point.Z),
            MaxZ = points.Max(point => point.Z)
        };
    }

    private static SlabLoopBounds? ProjectSlabLoop(CurveLoop loop, ManholeAssembly assembly)
    {
        var points = loop.SelectMany(curve => curve.Tessellate()).ToList();
        if (points.Count < 3) return null;
        return new SlabLoopBounds
        {
            MinU = points.Min(assembly.Frame.ToU), MaxU = points.Max(assembly.Frame.ToU),
            MinV = points.Min(assembly.Frame.ToV), MaxV = points.Max(assembly.Frame.ToV)
        };
    }

    private static void AddOpening(ICollection<OpeningEnvelope> openings, WallDescriptor wall, ElementId sourceId,
        double minAlong, double maxAlong, double minZ, double maxZ, bool isVirtual, string prefix)
    {
        minAlong = Math.Max(minAlong, wall.MinAlong);
        maxAlong = Math.Min(maxAlong, wall.MaxAlong);
        minZ = Math.Max(minZ, wall.MinZ);
        maxZ = Math.Min(maxZ, wall.MaxZ);
        if (maxAlong - minAlong <= 1e-6 || maxZ - minZ <= 1e-6) return;
        openings.Add(new OpeningEnvelope
        {
            Key = $"{prefix}-{wall.Number}-{openings.Count + 1}",
            SourceId = sourceId,
            WallNumber = wall.Number,
            MinAlong = minAlong,
            MaxAlong = maxAlong,
            MinZ = minZ,
            MaxZ = maxZ,
            IsVirtual = isVirtual
        });
    }

    private static void AddSlabOpening(ICollection<OpeningEnvelope> openings, ManholeComponent component,
        ElementId sourceId, double minU, double maxU, double minV, double maxV, bool isVirtual, string prefix)
    {
        minU = Math.Max(minU, component.MinU);
        maxU = Math.Min(maxU, component.MaxU);
        minV = Math.Max(minV, component.MinV);
        maxV = Math.Min(maxV, component.MaxV);
        if (maxU - minU <= 1e-6 || maxV - minV <= 1e-6) return;
        openings.Add(new OpeningEnvelope
        {
            Key = $"{prefix}-{component.Key}-{openings.Count + 1}", SourceId = sourceId, WallNumber = 0,
            Component = component.Kind, MinAlong = minU, MaxAlong = maxU, MinZ = minV, MaxZ = maxV,
            MinU = minU, MaxU = maxU, MinV = minV, MaxV = maxV, IsVirtual = isVirtual
        });
    }

    private static IReadOnlyList<OpeningEnvelope> Deduplicate(IEnumerable<OpeningEnvelope> openings)
    {
        var result = new List<OpeningEnvelope>();
        foreach (var opening in openings.OrderBy(item => item.WallNumber).ThenBy(item => item.MinAlong))
        {
            var duplicate = result.Any(item => item.WallNumber == opening.WallNumber && item.Component == opening.Component &&
                Math.Abs((item.MinAlong + item.MaxAlong) / 2 - (opening.MinAlong + opening.MaxAlong) / 2) < 0.05 &&
                Math.Abs((item.MinZ + item.MaxZ) / 2 - (opening.MinZ + opening.MaxZ) / 2) < 0.05);
            if (!duplicate) result.Add(opening);
        }
        return result;
    }

    private static IEnumerable<XYZ> BoxCorners(BoundingBoxXYZ box)
    {
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 1; y++)
        for (var z = 0; z <= 1; z++)
            yield return new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z);
    }

    private static bool ContainsAny(string text, params string[] fragments) =>
        fragments.Any(fragment => text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);

    private sealed class LoopBounds
    {
        public double MinAlong { get; init; }
        public double MaxAlong { get; init; }
        public double MinZ { get; init; }
        public double MaxZ { get; init; }
        public double Area => Math.Abs((MaxAlong - MinAlong) * (MaxZ - MinZ));
    }

    private sealed class SlabLoopBounds
    {
        public double MinU { get; init; }
        public double MaxU { get; init; }
        public double MinV { get; init; }
        public double MaxV { get; init; }
        public double Area => Math.Abs((MaxU - MinU) * (MaxV - MinV));
    }
}
