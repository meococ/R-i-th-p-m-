using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using BIM.DatViet.RevitServices;

namespace BIM.DatViet.Recognition;

public sealed class ManholeRecognitionResult
{
    public ManholeAssembly? Assembly { get; set; }
    public List<ValidationIssue> Issues { get; } = [];
    public bool Succeeded => Assembly is not null && Issues.All(issue => issue.Severity != ValidationSeverity.Error);
}

public sealed class ManholeRecognizer
{
    private static readonly HashSet<BuiltInCategory> CandidateCategories =
    [
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Parts
    ];

    private readonly Document _document;
    private readonly double _planeTolerance = UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters);
    private readonly double _contactTolerance = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);

    public ManholeRecognizer(Document document)
    {
        _document = document;
    }

    public ManholeRecognitionResult Resolve(Element picked) => Resolve([picked]);

    /// <summary>
    /// Recognize a manhole from one or many selected elements.
    /// <list type="bullet">
    /// <item>1 element: try single-family host, else auto-collect related (group/assembly/HOGA_ID/bbox).</item>
    /// <item>N elements: use the explicit selection as the multi-host seed (no silent BFS expansion).</item>
    /// </list>
    /// Structural Foundation / Floor / Part are valid bottom/top hosts; Wall hosts the vertical faces.
    /// </summary>
    public ManholeRecognitionResult Resolve(IReadOnlyList<Element> selected)
    {
        var result = new ManholeRecognitionResult();
        if (selected is null || selected.Count == 0)
        {
            result.Issues.Add(new ValidationIssue("PICK_EMPTY", ValidationSeverity.Error,
                "Chưa chọn element nào. Hãy chọn Wall / Structural Foundation / Floor / Part / Generic Model."));
            return result;
        }

        var unique = selected.GroupBy(element => element.Id).Select(group => group.First()).ToList();
        var invalid = unique.Where(element => !IsCandidate(element)).ToList();
        if (invalid.Count > 0)
        {
            result.Issues.Add(new ValidationIssue("PICK_CATEGORY_INVALID", ValidationSeverity.Error,
                "Có element không thuộc category hợp lệ (Wall, Structural Foundation, Floor, Part, Generic Model).",
                invalid.Select(element => element.Id).ToArray()));
        }

        var candidates = unique.Where(IsCandidate).ToList();
        if (candidates.Count == 0) return result;

        result.Issues.Add(new ValidationIssue("PICK_SUMMARY", ValidationSeverity.Info,
            BuildSelectionSummary(candidates),
            candidates.Select(element => element.Id).ToArray()));

        // Single pick: try complete family host first, then auto multi-host collect.
        if (candidates.Count == 1)
        {
            var seed = candidates[0];
            AddHostReadinessIssues([seed], result.Issues, ValidationSeverity.Error);

            if (seed is FamilyInstance && IsValidRebarHost(seed))
            {
                var single = TryResolveSingleHost(seed, result.Issues);
                if (single is not null)
                {
                    result.Assembly = single;
                    return result;
                }
            }

            var related = CollectRelatedElements(seed)
                .GroupBy(element => element.Id)
                .Select(group => group.First())
                .Where(IsCandidate)
                .ToList();
            result.Issues.Add(new ValidationIssue("AUTO_COLLECT", ValidationSeverity.Info,
                $"Đã gom tự động {related.Count} element liên quan từ 1 pick (group/assembly/HOGA_ID/tiếp xúc).",
                related.Select(element => element.Id).ToArray()));
            result.Assembly = TryResolveMultiHostFromCandidates(related, result.Issues, explicitSelection: false);
            return result;
        }

        // Multi-select is an exact assembly contract; every selected element must be one of the six hosts.
        AddHostReadinessIssues(candidates, result.Issues, ValidationSeverity.Error);
        result.Assembly = TryResolveMultiHostFromCandidates(candidates, result.Issues, explicitSelection: true);
        return result;
    }

    private ManholeAssembly? TryResolveSingleHost(Element host, ICollection<ValidationIssue> issues)
    {
        var solids = GeometryService.GetSolids(host);
        var vertices = GeometryService.GetVertices(solids);
        if (vertices.Count < 8) return null;

        var transform = host is FamilyInstance familyInstance ? familyInstance.GetTransform() : Transform.Identity;
        var u = Horizontal(transform.BasisX);
        var v = Horizontal(transform.BasisY);
        if (u.GetLength() < 0.9 || v.GetLength() < 0.9)
        {
            u = XYZ.BasisX;
            v = XYZ.BasisY;
        }

        var origin = transform.Origin;
        var frame = new LocalFrame(origin, u, v);
        var minU = vertices.Min(frame.ToU);
        var maxU = vertices.Max(frame.ToU);
        var minV = vertices.Min(frame.ToV);
        var maxV = vertices.Max(frame.ToV);
        var bottomZ = vertices.Min(point => point.Z);
        var topZ = vertices.Max(point => point.Z);

        var uPlanes = new List<double>();
        var vPlanes = new List<double>();
        var zPlanes = new List<double>();
        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planarFace) continue;
                var normal = planarFace.FaceNormal.Normalize();
                if (GeometryService.IsParallel(normal, frame.U, Degrees(1))) uPlanes.Add(frame.ToU(planarFace.Origin));
                else if (GeometryService.IsParallel(normal, frame.V, Degrees(1))) vPlanes.Add(frame.ToV(planarFace.Origin));
                else if (GeometryService.IsParallel(normal, XYZ.BasisZ, Degrees(1))) zPlanes.Add(planarFace.Origin.Z);
            }
        }

        var uniqueU = GeometryService.UniqueCoordinates(uPlanes, _planeTolerance);
        var uniqueV = GeometryService.UniqueCoordinates(vPlanes, _planeTolerance);
        var uniqueZ = GeometryService.UniqueCoordinates(zPlanes, _planeTolerance);
        if (uniqueU.Count < 4 || uniqueV.Count < 4 || uniqueZ.Count < 4)
        {
            issues.Add(new ValidationIssue("SINGLE_HOST_NOT_BOX", ValidationSeverity.Warning,
                "Family không có đủ các mặt để nhận diện đáy, bốn tường và nắp; chuyển sang nhận diện nhiều host.", host.Id));
            return null;
        }

        var innerMinU = uniqueU[1];
        var innerMaxU = uniqueU[^2];
        var innerMinV = uniqueV[1];
        var innerMaxV = uniqueV[^2];
        var bottomSlabTopZ = uniqueZ[1];
        var topSlabBottomZ = uniqueZ[^2];
        if (innerMinU >= innerMaxU || innerMinV >= innerMaxV || bottomSlabTopZ >= topSlabBottomZ) return null;

        var midZ = (bottomSlabTopZ + topSlabBottomZ) / 2;
        var midU = (innerMinU + innerMaxU) / 2;
        var midV = (innerMinV + innerMaxV) / 2;
        var walls = new List<WallDescriptor>
        {
            CreateVirtualWall(host.Id, frame.Of(maxU - (maxU - innerMaxU) / 2, midV, midZ), frame.U, frame.V,
                maxU - innerMaxU, origin.DotProduct(frame.V) + innerMinV, origin.DotProduct(frame.V) + innerMaxV,
                bottomZ, topZ),
            CreateVirtualWall(host.Id, frame.Of(midU, maxV - (maxV - innerMaxV) / 2, midZ), frame.V, -frame.U,
                maxV - innerMaxV, origin.DotProduct(-frame.U) - innerMaxU, origin.DotProduct(-frame.U) - innerMinU,
                bottomZ, topZ),
            CreateVirtualWall(host.Id, frame.Of(minU + (innerMinU - minU) / 2, midV, midZ), -frame.U, -frame.V,
                innerMinU - minU, origin.DotProduct(-frame.V) - innerMaxV, origin.DotProduct(-frame.V) - innerMinV,
                bottomZ, topZ),
            CreateVirtualWall(host.Id, frame.Of(midU, minV + (innerMinV - minV) / 2, midZ), -frame.V, frame.U,
                innerMinV - minV, origin.DotProduct(frame.U) + innerMinU, origin.DotProduct(frame.U) + innerMaxU,
                bottomZ, topZ)
        };
        NumberWalls(walls);
        ApplyStoredWallOne([host], walls);

        issues.Add(new ValidationIssue("SINGLE_HOST_MODE", ValidationSeverity.Info,
            "Đã nhận diện hố ga dạng một family có thể host Rebar.", host.Id));

        var bounds = GeometryService.GetBounds(host);
        return new ManholeAssembly
        {
            PrimaryHostId = host.Id,
            ManholeKey = ResolveManholeKey([host], host),
            Frame = frame,
            Bottom = CreateComponent(host, ManholeComponentKind.Bottom, "BOT", frame, bounds),
            Top = CreateComponent(host, ManholeComponentKind.Top, "TOP", frame, bounds),
            Walls = walls,
            SourceElementIds = [host.Id],
            MinU = minU,
            MaxU = maxU,
            MinV = minV,
            MaxV = maxV,
            BottomZ = bottomZ,
            BottomSlabTopZ = bottomSlabTopZ,
            TopSlabBottomZ = topSlabBottomZ,
            TopZ = topZ,
            IsSingleHost = true
        };
    }

    private ManholeAssembly? TryResolveMultiHostFromCandidates(
        IReadOnlyList<Element> candidates,
        ICollection<ValidationIssue> issues,
        bool explicitSelection)
    {
        var walls = candidates.OfType<Wall>().Where(IsValidRebarHost).ToList();
        // Structural Foundation, Floor, Part (and non-wall hosts) are valid bottom/top slabs.
        var slabs = candidates.Where(IsSlabLikeHost).Where(IsValidRebarHost)
            .Select(element => new { Element = element, Bounds = GeometryService.GetBounds(element) })
            .Where(item => item.Bounds is not null)
            .OrderBy(item => (item.Bounds!.Min.Z + item.Bounds.Max.Z) / 2)
            .ToList();

        if (candidates.Count != 6 || walls.Count != 4 || slabs.Count != 2)
        {
            var mode = explicitSelection ? "từ multi-select" : "sau khi gom tự động";
            var hint = explicitSelection
                ? "Hãy chọn đúng 4 Wall + 2 Structural Foundation/Floor/Part (đáy và nắp). Có thể chọn thêm không được."
                : "Thử multi-select đủ 6 host, hoặc gán cùng HOGA_ID / group / assembly cho cả cụm.";
            issues.Add(new ValidationIssue("ASSEMBLY_INCOMPLETE", ValidationSeverity.Error,
                $"Cụm hố ga không đủ ({mode}): cần đúng 4 Wall host-rebar + 2 sàn (Foundation/Floor/Part) host-rebar; " +
                $"hiện có {walls.Count} Wall và {slabs.Count} sàn hợp lệ / {candidates.Count} candidate. {hint}",
                candidates.Select(element => element.Id).ToArray()));
            AppendClassificationBreakdown(candidates, issues);
            return null;
        }

        var bottom = slabs.First();
        var top = slabs.Last();
        if (bottom.Element.Id == top.Element.Id)
        {
            issues.Add(new ValidationIssue("SLAB_CLASSIFICATION_FAILED", ValidationSeverity.Error,
                "Không phân biệt được đáy và nắp hố ga.", bottom.Element.Id));
            return null;
        }

        var wallData = walls.Select(wall => new { Wall = wall, Bounds = GeometryService.GetBounds(wall) })
            .Where(item => item.Bounds is not null).ToList();
        if (wallData.Count != 4)
        {
            issues.Add(new ValidationIssue("WALL_GEOMETRY_MISSING", ValidationSeverity.Error,
                "Không đọc được bounding box của đủ bốn Wall.", walls.Select(wall => wall.Id).ToArray()));
            return null;
        }
        var disconnectedWalls = wallData
            .Where(item => !GeometryService.Intersects(item.Bounds, bottom.Bounds, _contactTolerance) ||
                           !GeometryService.Intersects(item.Bounds, top.Bounds, _contactTolerance))
            .ToList();
        if (disconnectedWalls.Count > 0)
        {
            issues.Add(new ValidationIssue("ASSEMBLY_COHESION_FAILED", ValidationSeverity.Error,
                "Đáy, nắp và bốn Wall không tạo thành một cụm tiếp xúc liên tục.",
                disconnectedWalls.Select(item => item.Wall.Id)
                    .Concat([bottom.Element.Id, top.Element.Id]).ToArray()));
            return null;
        }
        var center = new XYZ(
            wallData.Average(item => (item.Bounds!.Min.X + item.Bounds.Max.X) / 2),
            wallData.Average(item => (item.Bounds!.Min.Y + item.Bounds.Max.Y) / 2),
            wallData.Average(item => (item.Bounds!.Min.Z + item.Bounds.Max.Z) / 2));

        var descriptors = new List<WallDescriptor>();
        foreach (var item in wallData)
        {
            if (item.Wall.Location is not LocationCurve locationCurve) continue;
            var start = locationCurve.Curve.GetEndPoint(0);
            var end = locationCurve.Curve.GetEndPoint(1);
            var tangent = Horizontal(end - start).Normalize();
            var midpoint = (start + end) / 2;
            var outward = Horizontal(midpoint - center);
            if (outward.GetLength() < 0.1) outward = Horizontal(item.Wall.Orientation);
            outward = outward.Normalize();
            if (Math.Abs(outward.DotProduct(tangent)) > 0.05) outward = XYZ.BasisZ.CrossProduct(tangent).Normalize();
            if ((midpoint - center).DotProduct(outward) < 0) outward = -outward;
            descriptors.Add(new WallDescriptor
            {
                HostId = item.Wall.Id,
                Center = new XYZ(midpoint.X, midpoint.Y, center.Z),
                Outward = outward,
                Tangent = tangent,
                Thickness = item.Wall.Width,
                MinAlong = Math.Min(start.DotProduct(tangent), end.DotProduct(tangent)),
                MaxAlong = Math.Max(start.DotProduct(tangent), end.DotProduct(tangent)),
                MinZ = item.Bounds!.Min.Z,
                MaxZ = item.Bounds.Max.Z,
            });
        }

        if (descriptors.Count != 4)
        {
            issues.Add(new ValidationIssue("WALL_LOCATION_INVALID", ValidationSeverity.Error,
                "Một hoặc nhiều Wall không có LocationCurve để lập khung thép.",
                walls.Select(wall => wall.Id).ToArray()));
            return null;
        }

        if (!ValidateRectangle(descriptors, issues)) return null;
        NumberWalls(descriptors);

        // Primary is always the bottom slab — independent of which member the user picked.
        var primaryHost = bottom.Element;
        var assemblyElements = walls.Cast<Element>()
            .Concat(slabs.Select(item => item.Element))
            .GroupBy(element => element.Id)
            .Select(group => group.First())
            .ToList();
        var sourceIds = assemblyElements.Select(element => element.Id).ToArray();
        ApplyStoredWallOne(assemblyElements, descriptors);

        var w1 = descriptors.Single(wall => wall.Number == 1);
        var u = w1.Tangent.Normalize();
        var v = w1.Outward.Normalize();
        var frame = new LocalFrame(center, u, v);
        var bottomComponent = CreateComponent(
            bottom.Element, ManholeComponentKind.Bottom, "BOT", frame, bottom.Bounds);
        var topComponent = CreateComponent(
            top.Element, ManholeComponentKind.Top, "TOP", frame, top.Bounds);
        var allVertices = assemblyElements
            .SelectMany(element => GeometryService.GetVertices(GeometryService.GetSolids(element))).ToList();
        if (allVertices.Count == 0)
        {
            issues.Add(new ValidationIssue("NO_SOLID_GEOMETRY", ValidationSeverity.Error,
                "Không đọc được solid của cụm hố ga.", candidates.Select(element => element.Id).ToArray()));
            return null;
        }

        var bottomLabel = CategoryLabel(bottom.Element);
        var topLabel = CategoryLabel(top.Element);
        issues.Add(new ValidationIssue("MULTI_HOST_MODE", ValidationSeverity.Info,
            $"Đã nhận diện hố ga multi-host: 4 Wall + đáy ({bottomLabel}) + nắp ({topLabel})" +
            (explicitSelection ? " từ multi-select." : " sau gom tự động."),
            candidates.Select(element => element.Id).ToArray()));

        return new ManholeAssembly
        {
            PrimaryHostId = primaryHost.Id,
            ManholeKey = ResolveManholeKey(assemblyElements, primaryHost),
            Frame = frame,
            Bottom = bottomComponent,
            Top = topComponent,
            Walls = descriptors,
            SourceElementIds = sourceIds,
            MinU = Math.Min(bottomComponent.MinU, topComponent.MinU),
            MaxU = Math.Max(bottomComponent.MaxU, topComponent.MaxU),
            MinV = Math.Min(bottomComponent.MinV, topComponent.MinV),
            MaxV = Math.Max(bottomComponent.MaxV, topComponent.MaxV),
            BottomZ = bottom.Bounds!.Min.Z,
            BottomSlabTopZ = bottom.Bounds.Max.Z,
            TopSlabBottomZ = top.Bounds!.Min.Z,
            TopZ = top.Bounds.Max.Z,
            IsSingleHost = false
        };
    }

    private IEnumerable<Element> CollectRelatedElements(Element picked)
    {
        if (picked.AssemblyInstanceId != ElementId.InvalidElementId &&
            _document.GetElement(picked.AssemblyInstanceId) is AssemblyInstance assembly)
        {
            return assembly.GetMemberIds().Select(_document.GetElement).Where(element => element is not null)!;
        }

        if (picked.GroupId != ElementId.InvalidElementId && _document.GetElement(picked.GroupId) is Group group)
        {
            return group.GetMemberIds().Select(_document.GetElement).Where(element => element is not null)!;
        }

        var hogaId = picked.LookupParameter("HOGA_ID")?.AsString();
        var documentCandidates = new FilteredElementCollector(_document).WhereElementIsNotElementType()
            .Where(IsCandidate).ToList();
        if (!string.IsNullOrWhiteSpace(hogaId))
        {
            return documentCandidates.Where(element =>
                string.Equals(element.LookupParameter("HOGA_ID")?.AsString(), hogaId, StringComparison.OrdinalIgnoreCase));
        }

        var byId = documentCandidates.ToDictionary(element => element.Id);
        var bounds = documentCandidates.ToDictionary(element => element.Id, GeometryService.GetBounds);
        var visited = new HashSet<ElementId> { picked.Id };
        var queue = new Queue<ElementId>();
        queue.Enqueue(picked.Id);
        while (queue.Count > 0 && visited.Count <= 32)
        {
            var currentId = queue.Dequeue();
            if (!bounds.TryGetValue(currentId, out var currentBounds)) continue;
            foreach (var candidate in documentCandidates)
            {
                if (visited.Contains(candidate.Id)) continue;
                if (!GeometryService.Intersects(currentBounds, bounds[candidate.Id], _contactTolerance)) continue;
                visited.Add(candidate.Id);
                queue.Enqueue(candidate.Id);
            }
        }

        return visited.Where(byId.ContainsKey).Select(id => byId[id]);
    }

    private bool ValidateRectangle(IReadOnlyList<WallDescriptor> walls, ICollection<ValidationIssue> issues)
    {
        if (walls.Count != 4) return false;
        var ordered = walls.OrderBy(wall => Math.Atan2(wall.Outward.X, wall.Outward.Y)).ToList();
        for (var index = 0; index < 4; index++)
        {
            var next = ordered[(index + 1) % 4];
            if (Math.Abs(ordered[index].Outward.DotProduct(next.Outward)) > Math.Sin(Degrees(1)))
            {
                issues.Add(new ValidationIssue("WALLS_NOT_RECTANGULAR", ValidationSeverity.Error,
                    "Bốn Wall không tạo thành footprint chữ nhật trong tolerance 1°.", walls.Select(wall => wall.HostId).ToArray()));
                return false;
            }
        }

        for (var index = 0; index < 2; index++)
        {
            if (Math.Abs(ordered[index].Outward.DotProduct(ordered[index + 2].Outward)) < Math.Cos(Degrees(0.5)))
            {
                issues.Add(new ValidationIssue("OPPOSITE_WALLS_NOT_PARALLEL", ValidationSeverity.Error,
                    "Hai cặp Wall đối diện không song song trong tolerance 0.5°.", walls.Select(wall => wall.HostId).ToArray()));
                return false;
            }
        }

        var joinTolerance = walls.Max(wall => wall.Thickness) / 2 +
                            UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters);
        for (var index = 0; index < 4; index++)
        {
            var first = ordered[index];
            var second = ordered[(index + 1) % 4];
            if (!TryIntersect2D(first.Center, first.Tangent, second.Center, second.Tangent, out var intersection) ||
                intersection.DotProduct(first.Tangent) < first.MinAlong - joinTolerance ||
                intersection.DotProduct(first.Tangent) > first.MaxAlong + joinTolerance ||
                intersection.DotProduct(second.Tangent) < second.MinAlong - joinTolerance ||
                intersection.DotProduct(second.Tangent) > second.MaxAlong + joinTolerance)
            {
                issues.Add(new ValidationIssue("WALL_LOOP_NOT_CLOSED", ValidationSeverity.Error,
                    "Bốn Wall không tạo thành vòng chữ nhật kín tại các góc.", walls.Select(wall => wall.HostId).ToArray()));
                return false;
            }
        }

        return true;
    }

    private static bool TryIntersect2D(XYZ p1, XYZ d1, XYZ p2, XYZ d2, out XYZ point)
    {
        var denominator = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denominator) < 1e-9) { point = XYZ.Zero; return false; }
        var delta = p2 - p1;
        var t = (delta.X * d2.Y - delta.Y * d2.X) / denominator;
        point = p1 + d1 * t;
        return true;
    }

    private static WallDescriptor CreateVirtualWall(ElementId hostId, XYZ center, XYZ outward, XYZ tangent,
        double thickness, double minAlong, double maxAlong, double minZ, double maxZ)
    {
        return new WallDescriptor
        {
            HostId = hostId,
            Center = center,
            Outward = outward.Normalize(),
            Tangent = tangent.Normalize(),
            Thickness = thickness,
            MinAlong = minAlong,
            MaxAlong = maxAlong,
            MinZ = minZ,
            MaxZ = maxZ
        };
    }

    private static ManholeComponent CreateComponent(
        Element host,
        ManholeComponentKind kind,
        string key,
        LocalFrame frame,
        BoundingBoxXYZ? bounds)
    {
        var points = GeometryService.GetVertices(GeometryService.GetSolids(host)).ToList();
        if (points.Count == 0 && bounds is not null)
        {
            points =
            [
                new XYZ(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
                new XYZ(bounds.Min.X, bounds.Max.Y, bounds.Min.Z),
                new XYZ(bounds.Max.X, bounds.Min.Y, bounds.Min.Z),
                new XYZ(bounds.Max.X, bounds.Max.Y, bounds.Min.Z),
                new XYZ(bounds.Min.X, bounds.Min.Y, bounds.Max.Z),
                new XYZ(bounds.Min.X, bounds.Max.Y, bounds.Max.Z),
                new XYZ(bounds.Max.X, bounds.Min.Y, bounds.Max.Z),
                new XYZ(bounds.Max.X, bounds.Max.Y, bounds.Max.Z)
            ];
        }
        return new ManholeComponent
        {
            Kind = kind,
            HostId = host.Id,
            Key = key,
            BoundingBox = bounds,
            MinU = points.Count == 0 ? 0 : points.Min(frame.ToU),
            MaxU = points.Count == 0 ? 0 : points.Max(frame.ToU),
            MinV = points.Count == 0 ? 0 : points.Min(frame.ToV),
            MaxV = points.Count == 0 ? 0 : points.Max(frame.ToV)
        };
    }

    private static void NumberWalls(IReadOnlyCollection<WallDescriptor> walls)
    {
        var ordered = walls.OrderBy(wall => NormalizeAngle(Math.Atan2(wall.Outward.X, wall.Outward.Y)))
            .ThenBy(wall => ElementIdCompat.ToLong(wall.HostId)).ToList();
        for (var index = 0; index < ordered.Count; index++) ordered[index].Number = index + 1;
    }

    public static void RotateWallNumbers(ManholeAssembly assembly)
    {
        foreach (var wall in assembly.Walls) wall.Number = wall.Number == 1 ? 4 : wall.Number - 1;
    }

    public static void SetWallOne(ManholeAssembly assembly, WallDescriptor selected)
    {
        while (selected.Number != 1) RotateWallNumbers(assembly);
    }

    private static void ApplyStoredWallOne(IEnumerable<Element> hosts, IReadOnlyCollection<WallDescriptor> walls)
    {
        foreach (var host in hosts)
        {
            if (!ManholeHostIdentityStore.TryRead(host, out _, out var saved) || saved.GetLength() < 0.5) continue;
            var selected = walls.OrderByDescending(wall => wall.Outward.DotProduct(saved.Normalize())).First();
            while (selected.Number != 1)
                foreach (var wall in walls) wall.Number = wall.Number == 1 ? 4 : wall.Number - 1;
            return;
        }
    }

    private static double NormalizeAngle(double angle) => angle < 0 ? angle + Math.PI * 2 : angle;
    private static double Degrees(double degrees) => degrees * Math.PI / 180;
    private static XYZ Horizontal(XYZ vector) => new(vector.X, vector.Y, 0);

    private static bool IsCandidate(Element element)
    {
        if (element.Category is null) return false;
        return CandidateCategories.Contains(
            (BuiltInCategory)ElementIdCompat.ToLong(element.Category.Id));
    }

    /// <summary>Bottom/top hosts: Structural Foundation, Floor, Part, or non-wall Generic Model.</summary>
    private static bool IsSlabLikeHost(Element element)
    {
        if (element is Wall) return false;
        if (element.Category is null) return false;
        var category = (BuiltInCategory)ElementIdCompat.ToLong(element.Category.Id);
        return category is BuiltInCategory.OST_StructuralFoundation
            or BuiltInCategory.OST_Floors
            or BuiltInCategory.OST_Parts
            or BuiltInCategory.OST_GenericModel;
    }

    private bool IsValidRebarHost(Element element)
    {
        if (!RebarHostData.IsValidHost(element)) return false;

        // Structural Wall / Foundation / Floor / Part that already host Rebar are accepted.
        // Vietnamese models often name materials "C25", "M300", "BTCT" without the word "concrete".
        if (IsStructuralCategoryHost(element)) return true;

        // Generic Model still requires an explicit concrete signal (Can Host Rebar alone is not enough).
        return HasConcreteMaterial(element);
    }

    private static bool IsStructuralCategoryHost(Element element)
    {
        if (element is Wall) return true;
        if (element.Category is null) return false;
        var category = (BuiltInCategory)ElementIdCompat.ToLong(element.Category.Id);
        return category is BuiltInCategory.OST_StructuralFoundation
            or BuiltInCategory.OST_Floors
            or BuiltInCategory.OST_Parts;
    }

    private bool HasConcreteMaterial(Element element)
    {
        if (element is FamilyInstance family &&
            family.StructuralMaterialType is StructuralMaterialType.Concrete or StructuralMaterialType.PrecastConcrete)
            return true;

        foreach (var material in EnumerateElementMaterials(element))
            if (IsConcreteMaterial(material)) return true;

        // Type / family name hints (e.g. "Foundation-Concrete", "BTCT D300").
        var typeName = _document.GetElement(element.GetTypeId())?.Name ?? string.Empty;
        var familyName = element is FamilyInstance fi ? fi.Symbol?.FamilyName ?? string.Empty : string.Empty;
        if (LooksLikeConcreteText($"{typeName} {familyName} {element.Name}")) return true;

        return false;
    }

    private IEnumerable<Material> EnumerateElementMaterials(Element element)
    {
        foreach (BuiltInParameter bip in new[]
                 {
                     BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
                     BuiltInParameter.MATERIAL_ID_PARAM
                 })
        {
            var id = element.get_Parameter(bip)?.AsElementId();
            if (id is not null && id != ElementId.InvalidElementId &&
                _document.GetElement(id) is Material material)
                yield return material;
        }

        // Wall type compound layers
        if (element is Wall wall && wall.WallType.GetCompoundStructure() is { } structure)
        {
            foreach (var layer in structure.GetLayers())
            {
                if (layer.MaterialId != ElementId.InvalidElementId &&
                    _document.GetElement(layer.MaterialId) is Material layerMaterial)
                    yield return layerMaterial;
            }
        }

        foreach (var materialId in element.GetMaterialIds(false))
        {
            if (_document.GetElement(materialId) is Material painted)
                yield return painted;
        }
    }

    private static bool IsConcreteMaterial(Material material) =>
        LooksLikeConcreteText($"{material.Name} {material.MaterialClass} {material.MaterialCategory}");

    private static bool LooksLikeConcreteText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.ToUpperInvariant()
            .Replace('Ê', 'E')
            .Replace('Ô', 'O')
            .Replace('Ơ', 'O')
            .Replace('Ư', 'U')
            .Replace('Ă', 'A')
            .Replace('Á', 'A')
            .Replace('À', 'A')
            .Replace('Ả', 'A')
            .Replace('Ã', 'A')
            .Replace('Ạ', 'A');

        // Collapse spaces for matching "BE TONG" / "BÊ TÔNG"
        var compact = text.Replace(" ", string.Empty);

        if (text.Contains("CONCRETE") || text.Contains("PRECAST") || text.Contains("C.I.P") ||
            text.Contains("CAST-IN") || text.Contains("CAST IN") || text.Contains("CIP"))
            return true;

        if (compact.Contains("BETONG") || compact.Contains("BTCT") || compact.Contains("BETHONG") ||
            compact.Contains("BE TONG".Replace(" ", string.Empty)))
            return true;

        // Grade-style names common in VN models: C20, C25, C30, M200, M250, M300
        if (System.Text.RegularExpressions.Regex.IsMatch(compact, @"(^|[^A-Z0-9])C(20|25|30|35|40|45|50|55|60)([^A-Z0-9]|$)") ||
            System.Text.RegularExpressions.Regex.IsMatch(compact, @"(^|[^A-Z0-9])M(150|200|250|300|350|400)([^A-Z0-9]|$)"))
            return true;

        return false;
    }

    private void AddHostReadinessIssues(
        IReadOnlyList<Element> elements,
        ICollection<ValidationIssue> issues,
        ValidationSeverity severity)
    {
        foreach (var element in elements)
        {
            if (!RebarHostData.IsValidHost(element))
            {
                issues.Add(new ValidationIssue("HOST_CANNOT_HOST_REBAR", severity,
                    $"{CategoryLabel(element)} Id {ElementIdCompat.ToLong(element.Id)} chưa bật / không thể host Rebar (Can Host Rebar).",
                    element.Id));
                continue;
            }

            // Structural categories that host rebar: only soft-warn if material text is unclear.
            if (IsStructuralCategoryHost(element))
            {
                if (!HasConcreteMaterial(element))
                {
                    issues.Add(new ValidationIssue("HOST_MATERIAL_UNCLEAR", ValidationSeverity.Warning,
                        $"{CategoryLabel(element)} Id {ElementIdCompat.ToLong(element.Id)} host Rebar OK nhưng tên vật liệu không rõ bê tông " +
                        "(vẫn chấp nhận vì category kết cấu).",
                        element.Id));
                }
                continue;
            }

            if (!HasConcreteMaterial(element))
            {
                issues.Add(new ValidationIssue("HOST_MATERIAL_NOT_CONCRETE", severity,
                    $"{CategoryLabel(element)} Id {ElementIdCompat.ToLong(element.Id)} chưa có vật liệu bê tông/precast (Structural Material).",
                    element.Id));
            }
        }
    }

    private void AppendClassificationBreakdown(IReadOnlyList<Element> candidates, ICollection<ValidationIssue> issues)
    {
        var wallTotal = candidates.OfType<Wall>().Count();
        var wallOk = candidates.OfType<Wall>().Count(IsValidRebarHost);
        var foundation = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_StructuralFoundation);
        var foundationOk = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_StructuralFoundation && IsValidRebarHost(element));
        var floor = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Floors);
        var floorOk = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Floors && IsValidRebarHost(element));
        var part = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Parts);
        var partOk = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Parts && IsValidRebarHost(element));
        var generic = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_GenericModel);

        issues.Add(new ValidationIssue("ASSEMBLY_BREAKDOWN", ValidationSeverity.Warning,
            $"Phân loại: Wall {wallOk}/{wallTotal} hợp lệ; Structural Foundation {foundationOk}/{foundation}; " +
            $"Floor {floorOk}/{floor}; Part {partOk}/{part}; Generic Model {generic}. " +
            "Sàn đáy/nắp hợp lệ = Foundation/Floor/Part (hoặc GM) có Can Host Rebar + bê tông.",
            candidates.Select(element => element.Id).ToArray()));

        foreach (var element in candidates.Where(element => !IsValidRebarHost(element)))
        {
            var reason = !RebarHostData.IsValidHost(element)
                ? "không host Rebar"
                : "vật liệu không nhận là bê tông";
            issues.Add(new ValidationIssue("CANDIDATE_REJECTED", ValidationSeverity.Warning,
                $"{CategoryLabel(element)} Id {ElementIdCompat.ToLong(element.Id)} bị loại: {reason}.",
                element.Id));
        }
    }

    private static string BuildSelectionSummary(IReadOnlyList<Element> candidates)
    {
        var walls = candidates.OfType<Wall>().Count();
        var foundations = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_StructuralFoundation);
        var floors = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Floors);
        var parts = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_Parts);
        var generics = candidates.Count(element =>
            (element.Category is null ? long.MinValue : ElementIdCompat.ToLong(element.Category.Id)) == (long)BuiltInCategory.OST_GenericModel);
        return $"Đã chọn {candidates.Count} element: Wall={walls}, Structural Foundation={foundations}, " +
               $"Floor={floors}, Part={parts}, Generic Model={generics}.";
    }

    private static string CategoryLabel(Element element) =>
        element.Category?.Name ?? element.GetType().Name;

    /// <summary>
    /// Stable manhole key independent of which assembly member was picked.
    /// Preference: host identity → HOGA_ID → Mark → HG-{preferred stable host id}.
    /// </summary>
    private static string ResolveManholeKey(IReadOnlyList<Element> members, Element preferred)
    {
        foreach (var element in members.OrderBy(item => ElementIdCompat.ToLong(item.Id)))
        {
            if (ManholeHostIdentityStore.TryRead(element, out var storedKey, out _) &&
                !string.IsNullOrWhiteSpace(storedKey))
                return Sanitize(storedKey);
        }

        foreach (var element in members.OrderBy(item => ElementIdCompat.ToLong(item.Id)))
        {
            var hogaId = element.LookupParameter("HOGA_ID")?.AsString();
            if (!string.IsNullOrWhiteSpace(hogaId)) return Sanitize(hogaId!);
        }

        foreach (var element in members.OrderBy(item => ElementIdCompat.ToLong(item.Id)))
        {
            var mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            if (!string.IsNullOrWhiteSpace(mark)) return Sanitize(mark!);
        }

        return $"HG-{ElementIdCompat.ToLong(preferred.Id)}";
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().ToUpperInvariant().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray();
        return new string(chars);
    }
}
