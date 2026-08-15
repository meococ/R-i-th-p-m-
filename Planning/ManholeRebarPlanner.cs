using Autodesk.Revit.DB;
using BIM.DatViet.Models;
using BIM.DatViet.RevitServices;
using BIM.DatViet.Domain;

namespace BIM.DatViet.Planning;

public sealed class ManholeRebarPlanner
{
    public RebarPlan Build(ManholeAssembly assembly, ManholeRebarPreset preset, RebarTypeCatalog catalog)
    {
        var plan = new RebarPlan { Assembly = assembly, SpecHash = preset.ComputeHash() };
        plan.Issues.AddRange(ValidatePreset(preset));

        var slabType = ResolveType(catalog, preset.SlabBarTypeName, "TYPE_SLAB_MISSING", plan.Issues);
        var verticalType = ResolveType(catalog, preset.WallVerticalBarTypeName, "TYPE_WALL_V_MISSING", plan.Issues);
        var horizontalType = ResolveType(catalog, preset.WallHorizontalBarTypeName, "TYPE_WALL_H_MISSING", plan.Issues);
        var openingType = ResolveType(catalog, preset.OpeningBarTypeName, "TYPE_OPENING_MISSING", plan.Issues);
        if (slabType is null || verticalType is null || horizontalType is null || openingType is null) return plan;

        var cover = Mm(preset.CoverMm);
        PlanSlab(plan, assembly.Bottom, "BOT", assembly.BottomZ, assembly.BottomSlabTopZ,
            slabType, preset, cover);
        PlanSlab(plan, assembly.Top, "TOP", assembly.TopSlabBottomZ, assembly.TopZ,
            slabType, preset, cover);

        foreach (var wall in assembly.Walls.OrderBy(item => item.Number))
            PlanWall(plan, wall, preset, verticalType, horizontalType, openingType);
        PlanCorners(plan, preset, horizontalType);
        ValidatePlanGeometry(plan);

        foreach (var opening in assembly.Openings.Where(item => item.IsVirtual))
            plan.Issues.Add(new ValidationIssue("VIRTUAL_OPENING", ValidationSeverity.Warning,
                $"{opening.Key} được suy ra từ MEP/sleeve nhưng chưa có void cắt thật.", opening.SourceId));
        if (!preset.EngineerConfirmed)
            plan.Issues.Add(new ValidationIssue("ENGINEER_CONFIRMATION_REQUIRED", ValidationSeverity.Error,
                "Preset chỉ được phép preview. Hãy xác nhận thông số kỹ sư trước khi tạo thép.")); ///xóa không dùng dòng này nửa
        return plan;
    }

    private static void PlanSlab(RebarPlan plan, ManholeComponent component, string componentKey,
        double bottomZ, double topZ, RebarTypeInfo type, ManholeRebarPreset preset, double cover)
    {
        var assembly = plan.Assembly;
        var radius = type.Diameter / 2;
        var minU = component.MinU + cover + radius;
        var maxU = component.MaxU - cover - radius;
        var minV = component.MinV + cover + radius;
        var maxV = component.MaxV - cover - radius;
        if (minU >= maxU || minV >= maxV || bottomZ + cover + type.Diameter >= topZ - cover)
        {
            plan.Issues.Add(new ValidationIssue("SLAB_COVER_IMPOSSIBLE", ValidationSeverity.Error,
                $"Không đủ kích thước để bố trí hai lớp thép {componentKey} với cover đã chọn.", component.HostId));
            return;
        }

        var lowerU = bottomZ + cover + radius;
        var lowerV = lowerU + type.Diameter;
        var upperU = topZ - cover - radius;
        var upperV = upperU - type.Diameter;
        var openings = assembly.Openings.Where(opening => opening.Component == component.Kind && opening.WallNumber == 0).ToList();
        var spacing = Mm(preset.SlabSpacingMm);
        if (openings.Count == 0)
        {
            AddSlabSet(plan, component, componentKey, RebarFace.Bottom, RebarDirection.U, type, spacing,
                assembly.Frame.Of(minU, minV, lowerU), assembly.Frame.Of(maxU, minV, lowerU), assembly.Frame.V, maxV - minV);
            AddSlabSet(plan, component, componentKey, RebarFace.Bottom, RebarDirection.V, type, spacing,
                assembly.Frame.Of(minU, minV, lowerV), assembly.Frame.Of(minU, maxV, lowerV), assembly.Frame.U, maxU - minU);
            AddSlabSet(plan, component, componentKey, RebarFace.Top, RebarDirection.U, type, spacing,
                assembly.Frame.Of(minU, minV, upperU), assembly.Frame.Of(maxU, minV, upperU), assembly.Frame.V, maxV - minV);
            AddSlabSet(plan, component, componentKey, RebarFace.Top, RebarDirection.V, type, spacing,
                assembly.Frame.Of(minU, minV, upperV), assembly.Frame.Of(minU, maxV, upperV), assembly.Frame.U, maxU - minU);
            return;
        }

        foreach (var layer in new[]
                 {
                     (Face: RebarFace.Bottom, Direction: RebarDirection.U, Z: lowerU),
                     (Face: RebarFace.Bottom, Direction: RebarDirection.V, Z: lowerV),
                     (Face: RebarFace.Top, Direction: RebarDirection.U, Z: upperU),
                     (Face: RebarFace.Top, Direction: RebarDirection.V, Z: upperV)
                 })
            AddClippedSlabGrid(plan, component, componentKey, layer.Face, layer.Direction, layer.Z,
                type, spacing, Mm(preset.MinRetainedLengthMm), minU, maxU, minV, maxV, openings);

        foreach (var faceAndZ in new[] { (Face: RebarFace.Bottom, Z: lowerU), (Face: RebarFace.Top, Z: upperU) })
        foreach (var opening in openings)
            AddSlabOpeningBars(plan, component, componentKey, faceAndZ.Face, faceAndZ.Z, type, preset,
                opening, minU, maxU, minV, maxV);
    }

    private static void AddClippedSlabGrid(RebarPlan plan, ManholeComponent component, string componentKey,
        RebarFace face, RebarDirection direction, double z, RebarTypeInfo type, double spacing,
        double minimumLength, double minU, double maxU, double minV, double maxV,
        IReadOnlyCollection<OpeningEnvelope> openings)
    {
        var distributionMin = direction == RebarDirection.U ? minV : minU;
        var distributionMax = direction == RebarDirection.U ? maxV : maxU;
        foreach (var distribution in EvenPositions(distributionMin, distributionMax, spacing))
        {
            var exclusions = direction == RebarDirection.U
                ? openings.Where(o => distribution >= o.MinV && distribution <= o.MaxV).Select(o => (o.MinU, o.MaxU))
                : openings.Where(o => distribution >= o.MinU && distribution <= o.MaxU).Select(o => (o.MinV, o.MaxV));
            var lineMin = direction == RebarDirection.U ? minU : minV;
            var lineMax = direction == RebarDirection.U ? maxU : maxV;
            foreach (var interval in ClipInterval(lineMin, lineMax, exclusions, minimumLength))
            {
                var start = direction == RebarDirection.U
                    ? plan.Assembly.Frame.Of(interval.Start, distribution, z)
                    : plan.Assembly.Frame.Of(distribution, interval.Start, z);
                var end = direction == RebarDirection.U
                    ? plan.Assembly.Frame.Of(interval.End, distribution, z)
                    : plan.Assembly.Frame.Of(distribution, interval.End, z);
                AddSlabStraight(plan, component, componentKey, face, direction, type, start, end, false);
            }
        }
    }

    private static void AddSlabOpeningBars(RebarPlan plan, ManholeComponent component, string componentKey,
        RebarFace face, double z, RebarTypeInfo type, ManholeRebarPreset preset, OpeningEnvelope opening,
        double minU, double maxU, double minV, double maxV)
    {
        var extension = Mm(preset.MinRetainedLengthMm);
        for (var index = 0; index < Math.Max(1, preset.OpeningEdgeBarCount); index++)
        {
            var offset = index * type.Diameter;
            var u0 = Math.Max(minU, opening.MinU - extension);
            var u1 = Math.Min(maxU, opening.MaxU + extension);
            var v0 = Math.Max(minV, opening.MinV - extension);
            var v1 = Math.Min(maxV, opening.MaxV + extension);
            foreach (var v in new[] { opening.MinV - offset, opening.MaxV + offset }.Where(v => v >= minV && v <= maxV))
                AddSlabStraight(plan, component, componentKey, face, RebarDirection.Opening, type,
                    plan.Assembly.Frame.Of(u0, v, z), plan.Assembly.Frame.Of(u1, v, z), true);
            foreach (var u in new[] { opening.MinU - offset, opening.MaxU + offset }.Where(u => u >= minU && u <= maxU))
                AddSlabStraight(plan, component, componentKey, face, RebarDirection.Opening, type,
                    plan.Assembly.Frame.Of(u, v0, z), plan.Assembly.Frame.Of(u, v1, z), true);
        }
    }

    private static void AddSlabStraight(RebarPlan plan, ManholeComponent component, string componentKey,
        RebarFace face, RebarDirection direction, RebarTypeInfo type, XYZ start, XYZ end, bool opening)
    {
        if (start.DistanceTo(end) < 1e-6) return;
        plan.Bars.Add(new PlannedBar
        {
            Key = $"{componentKey}-{face}-{direction}-{plan.Bars.Count}", HostId = component.HostId,
            BarTypeName = type.Name,
            Partition = $"{plan.Assembly.ManholeKey}-{componentKey}-{(opening ? "OPENING" : FaceCode(face) + "-" + direction)}",
            Component = component.Kind, Face = face, Direction = direction, Points = [start, end],
            PlaneNormal = XYZ.BasisZ, IsOpeningBar = opening
        });
    }

    private static void AddSlabSet(RebarPlan plan, ManholeComponent component, string componentKey, RebarFace face,
        RebarDirection direction, RebarTypeInfo type, double spacing, XYZ start, XYZ end, XYZ distribution, double length)
    {
        var quantity = CountPositions(length, spacing);
        plan.Bars.Add(new PlannedBar
        {
            Key = $"{componentKey}-{face}-{direction}", HostId = component.HostId, BarTypeName = type.Name,
            Partition = $"{plan.Assembly.ManholeKey}-{componentKey}-{FaceCode(face)}-{direction}",
            Component = component.Kind, Face = face, Direction = direction, Points = [start, end],
            PlaneNormal = distribution.Normalize(), IsSet = true, SetLength = length, MaxSpacing = spacing,
            PlannedQuantity = quantity
        });
    }

    private static void PlanWall(RebarPlan plan, WallDescriptor wall, ManholeRebarPreset preset,
        RebarTypeInfo verticalType, RebarTypeInfo horizontalType, RebarTypeInfo openingType)
    {
        var assembly = plan.Assembly;
        var cover = Mm(preset.CoverMm);
        var minLength = Mm(preset.MinRetainedLengthMm);
        var alongMin = wall.MinAlong + cover + verticalType.Diameter / 2;
        var alongMax = wall.MaxAlong - cover - verticalType.Diameter / 2;
        var verticalBottom = assembly.BottomZ + cover + verticalType.Diameter / 2;
        var verticalTop = assembly.TopZ - cover - verticalType.Diameter / 2;
        var horizontalBottom = assembly.BottomSlabTopZ + Mm(preset.EndClearanceMm) + horizontalType.Diameter / 2;
        var horizontalTop = assembly.TopSlabBottomZ - Mm(preset.EndClearanceMm) - horizontalType.Diameter / 2;
        if (alongMin >= alongMax || verticalBottom >= verticalTop || horizontalBottom >= horizontalTop)
        {
            plan.Issues.Add(new ValidationIssue("WALL_LAYOUT_IMPOSSIBLE", ValidationSeverity.Error,
                $"W{wall.Number} không đủ không gian cho cover/layer đã chọn.", wall.HostId));
            return;
        }
        var requiredPerFace = cover + verticalType.Diameter + horizontalType.Diameter + Mm(preset.LayerGapMm);
        if (2 * requiredPerFace >= wall.Thickness)
        {
            plan.Issues.Add(new ValidationIssue("WALL_THICKNESS_INSUFFICIENT", ValidationSeverity.Error,
                $"W{wall.Number} không đủ chiều dày cho hai mặt thép và khoảng hở lớp.", wall.HostId));
            return;
        }

        var wallOpenings = assembly.Openings.Where(item => item.WallNumber == wall.Number).ToList();
        foreach (var face in new[] { RebarFace.Inner, RebarFace.Outer })
        {
            var verticalPlane = WallPlanePoint(wall, face, cover + verticalType.Diameter / 2);
            var horizontalPlane = WallPlanePoint(wall, face,
                cover + verticalType.Diameter + horizontalType.Diameter / 2 + Mm(preset.LayerGapMm));
            if (wallOpenings.Count == 0)
            {
                // Vertical: (along, z0, z1) distributed along wall.Tangent.
                AddWallSet(plan, wall, face, RebarDirection.Vertical, verticalType,
                    verticalPlane, alongMin, verticalBottom, verticalTop, wall.Tangent,
                    alongMax - alongMin, Mm(preset.WallVerticalSpacingMm));
                // Horizontal: (along0, z, along1) distributed along BasisZ — must match WallSetCenterline.
                AddWallSet(plan, wall, face, RebarDirection.Horizontal, horizontalType,
                    horizontalPlane, alongMin, horizontalBottom, alongMax, XYZ.BasisZ,
                    horizontalTop - horizontalBottom, Mm(preset.WallHorizontalSpacingMm));
            }
            else
            {
                foreach (var along in EvenPositions(alongMin, alongMax, Mm(preset.WallVerticalSpacingMm)))
                {
                    var intervals = ClipInterval(verticalBottom, verticalTop,
                        wallOpenings.Where(opening => along >= opening.MinAlong && along <= opening.MaxAlong)
                            .Select(opening => (opening.MinZ, opening.MaxZ)), minLength);
                    foreach (var interval in intervals)
                        AddStraight(plan, wall, face, RebarDirection.Vertical, verticalType, verticalPlane,
                            along, interval.Start, along, interval.End, false);
                }

                foreach (var z in EvenPositions(horizontalBottom, horizontalTop, Mm(preset.WallHorizontalSpacingMm)))
                {
                    var intervals = ClipInterval(alongMin, alongMax,
                        wallOpenings.Where(opening => z >= opening.MinZ && z <= opening.MaxZ)
                            .Select(opening => (opening.MinAlong, opening.MaxAlong)), minLength);
                    foreach (var interval in intervals)
                        AddStraight(plan, wall, face, RebarDirection.Horizontal, horizontalType, horizontalPlane,
                            interval.Start, z, interval.End, z, false);
                }
            }

            foreach (var opening in wallOpenings)
                AddOpeningBars(plan, wall, face, opening, preset, openingType, verticalPlane, horizontalPlane,
                    alongMin, alongMax, verticalBottom, verticalTop);
        }
    }

    private static void AddWallSet(RebarPlan plan, WallDescriptor wall, RebarFace face, RebarDirection direction,
        RebarTypeInfo type, XYZ planePoint, double first, double second, double third, XYZ distribution,
        double setLength, double spacing)
    {
        var (startAlong, startZ, endAlong, endZ) = GeometryKernel.WallSetCenterline(
            direction == RebarDirection.Vertical, first, second, third);
        var start = PointOnWall(wall, planePoint, startAlong, startZ);
        var end = PointOnWall(wall, planePoint, endAlong, endZ);
        plan.Bars.Add(new PlannedBar
        {
            Key = $"W{wall.Number}-{face}-{direction}", HostId = wall.HostId, BarTypeName = type.Name,
            Partition = $"{plan.Assembly.ManholeKey}-W{wall.Number}-{FaceCode(face)}-{DirectionCode(direction)}",
            Component = ManholeComponentKind.Wall, Face = face, Direction = direction,
            Points = [start, end], PlaneNormal = distribution.Normalize(), WallNumber = wall.Number,
            IsSet = true, SetLength = setLength, MaxSpacing = spacing, PlannedQuantity = CountPositions(setLength, spacing)
        });
    }

    private static void AddStraight(RebarPlan plan, WallDescriptor wall, RebarFace face, RebarDirection direction,
        RebarTypeInfo type, XYZ planePoint, double startAlong, double startZ, double endAlong, double endZ, bool opening)
    {
        var start = PointOnWall(wall, planePoint, startAlong, startZ);
        var end = PointOnWall(wall, planePoint, endAlong, endZ);
        if (start.DistanceTo(end) < 1e-6) return;
        plan.Bars.Add(new PlannedBar
        {
            Key = $"W{wall.Number}-{face}-{direction}-{plan.Bars.Count}", HostId = wall.HostId,
            BarTypeName = type.Name,
            Partition = $"{plan.Assembly.ManholeKey}-W{wall.Number}-{(opening ? "OPENING" : FaceCode(face) + "-" + DirectionCode(direction))}",
            Component = ManholeComponentKind.Wall, Face = face, Direction = opening ? RebarDirection.Opening : direction,
            Points = [start, end], PlaneNormal = direction == RebarDirection.Vertical ? wall.Tangent : XYZ.BasisZ,
            WallNumber = wall.Number, IsOpeningBar = opening
        });
    }

    private static void AddOpeningBars(RebarPlan plan, WallDescriptor wall, RebarFace face, OpeningEnvelope opening,
        ManholeRebarPreset preset, RebarTypeInfo type, XYZ verticalPlane, XYZ horizontalPlane,
        double alongMin, double alongMax, double verticalBottom, double verticalTop)
    {
        var extension = Mm(preset.MinRetainedLengthMm);
        var z0 = Math.Max(verticalBottom, opening.MinZ - extension);
        var z1 = Math.Min(verticalTop, opening.MaxZ + extension);
        var t0 = Math.Max(alongMin, opening.MinAlong - extension);
        var t1 = Math.Min(alongMax, opening.MaxAlong + extension);
        for (var index = 0; index < Math.Max(1, preset.OpeningEdgeBarCount); index++)
        {
            var offset = index * type.Diameter;
            var left = opening.MinAlong - offset;
            var right = opening.MaxAlong + offset;
            var lower = opening.MinZ - offset;
            var upper = opening.MaxZ + offset;
            if (left >= alongMin && left <= alongMax) AddStraight(plan, wall, face, RebarDirection.Vertical, type, verticalPlane, left, z0, left, z1, true);
            if (right >= alongMin && right <= alongMax) AddStraight(plan, wall, face, RebarDirection.Vertical, type, verticalPlane, right, z0, right, z1, true);
            if (lower >= verticalBottom && lower <= verticalTop) AddStraight(plan, wall, face, RebarDirection.Horizontal, type, horizontalPlane, t0, lower, t1, lower, true);
            if (upper >= verticalBottom && upper <= verticalTop) AddStraight(plan, wall, face, RebarDirection.Horizontal, type, horizontalPlane, t0, upper, t1, upper, true);
        }
        if (preset.AddDiagonalOpeningBars)
        {
            var diagonal = Mm(preset.CornerLegMm);
            var candidates = new[]
            {
                (A: (opening.MinAlong - diagonal, opening.MinZ), B: (opening.MinAlong, opening.MinZ - diagonal)),
                (A: (opening.MaxAlong, opening.MinZ - diagonal), B: (opening.MaxAlong + diagonal, opening.MinZ)),
                (A: (opening.MaxAlong + diagonal, opening.MaxZ), B: (opening.MaxAlong, opening.MaxZ + diagonal)),
                (A: (opening.MinAlong, opening.MaxZ + diagonal), B: (opening.MinAlong - diagonal, opening.MaxZ))
            };
            foreach (var candidate in candidates)
            {
                var a = (Along: Math.Clamp(candidate.A.Item1, alongMin, alongMax), Z: Math.Clamp(candidate.A.Item2, verticalBottom, verticalTop));
                var b = (Along: Math.Clamp(candidate.B.Item1, alongMin, alongMax), Z: Math.Clamp(candidate.B.Item2, verticalBottom, verticalTop));
                AddOpeningDiagonal(plan, wall, face, type, verticalPlane, a, b);
            }
        }
    }

    private static void AddOpeningDiagonal(RebarPlan plan, WallDescriptor wall, RebarFace face,
        RebarTypeInfo type, XYZ planePoint, (double Along, double Z) start, (double Along, double Z) end)
    {
        var point0 = PointOnWall(wall, planePoint, start.Along, start.Z);
        var point1 = PointOnWall(wall, planePoint, end.Along, end.Z);
        if (point0.DistanceTo(point1) < 1e-6) return;
        plan.Bars.Add(new PlannedBar
        {
            Key = $"W{wall.Number}-{face}-OPENING-D-{plan.Bars.Count}", HostId = wall.HostId,
            BarTypeName = type.Name, Partition = $"{plan.Assembly.ManholeKey}-W{wall.Number}-OPENING",
            Component = ManholeComponentKind.Wall, Face = face, Direction = RebarDirection.Opening,
            Points = [point0, point1], PlaneNormal = wall.Outward, WallNumber = wall.Number, IsOpeningBar = true
        });
    }

    private static void PlanCorners(RebarPlan plan, ManholeRebarPreset preset, RebarTypeInfo type)
    {
        var assembly = plan.Assembly;
        var zMin = assembly.BottomSlabTopZ + Mm(preset.EndClearanceMm) + type.Diameter / 2;
        var zMax = assembly.TopSlabBottomZ - Mm(preset.EndClearanceMm) - type.Diameter / 2;
        if (zMin >= zMax) return;
        var skippedNearOpening = 0;
        foreach (var wall in assembly.Walls.OrderBy(item => item.Number))
        {
            var nextNumber = wall.Number == 4 ? 1 : wall.Number + 1;
            var next = assembly.Walls.Single(item => item.Number == nextNumber);
            // Openings on either wall of this corner (L-bar spans both).
            var cornerOpenings = assembly.Openings
                .Where(opening => opening.WallNumber == wall.Number || opening.WallNumber == nextNumber)
                .ToList();
            foreach (var face in new[] { RebarFace.Inner, RebarFace.Outer })
            {
                var offset = Mm(preset.CoverMm) + type.Diameter + type.Diameter / 2 + Mm(preset.LayerGapMm);
                var firstLinePoint = WallPlanePoint(wall, face, offset);
                var secondLinePoint = WallPlanePoint(next, face, offset);
                if (!TryIntersectLines(firstLinePoint, wall.Tangent, secondLinePoint, next.Tangent, out var corner)) continue;
                var firstDirection = Horizontal(wall.Center - corner).Normalize();
                var secondDirection = Horizontal(next.Center - corner).Normalize();
                var leg = Mm(preset.CornerLegMm);
                foreach (var z in EvenPositions(zMin, zMax, Mm(preset.WallHorizontalSpacingMm)))
                {
                    for (var copy = 0; copy < Math.Max(1, preset.CornerBarCount); copy++)
                    {
                        var shiftDirection = (firstDirection + secondDirection).Normalize();
                        var shifted = corner + shiftDirection * (copy * type.Diameter);
                        var point0 = new XYZ(shifted.X + firstDirection.X * leg, shifted.Y + firstDirection.Y * leg, z);
                        var point1 = new XYZ(shifted.X, shifted.Y, z);
                        var point2 = new XYZ(shifted.X + secondDirection.X * leg, shifted.Y + secondDirection.Y * leg, z);
                        if (CornerPolylineHitsOpening([point0, point1, point2], wall, next, cornerOpenings))
                        {
                            skippedNearOpening++;
                            continue;
                        }
                        plan.Bars.Add(new PlannedBar
                        {
                            Key = $"C{wall.Number}{next.Number}-{face}-{plan.Bars.Count}", HostId = wall.HostId,
                            BarTypeName = type.Name, Partition = $"{assembly.ManholeKey}-C{wall.Number}{next.Number}-{FaceCode(face)}",
                            Component = ManholeComponentKind.Wall, Face = face, Direction = RebarDirection.Corner,
                            Points = [point0, point1, point2], PlaneNormal = XYZ.BasisZ, WallNumber = wall.Number
                        });
                    }
                }
            }
        }
        if (skippedNearOpening > 0)
            plan.Issues.Add(new ValidationIssue("CORNER_SKIPPED_NEAR_OPENING", ValidationSeverity.Info,
                $"Đã bỏ {skippedNearOpening} thanh góc L tại cao độ/vùng giao opening (để không đâm lỗ)."));
    }

    /// <summary>
    /// True if any L-bar segment intersects an opening envelope on either adjacent wall
    /// (projected to that wall's along–Z plane).
    /// </summary>
    private static bool CornerPolylineHitsOpening(
        IReadOnlyList<XYZ> points,
        WallDescriptor wall,
        WallDescriptor next,
        IReadOnlyList<OpeningEnvelope> openings)
    {
        if (openings.Count == 0 || points.Count < 2) return false;
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            foreach (var opening in openings)
            {
                var host = opening.WallNumber == wall.Number ? wall : next;
                if (opening.WallNumber != wall.Number && opening.WallNumber != next.Number) continue;
                var a = (X: start.DotProduct(host.Tangent), Y: start.Z);
                var b = (X: end.DotProduct(host.Tangent), Y: end.Z);
                if (IntersectsOpenRectangle(a, b, opening.MinAlong, opening.MaxAlong, opening.MinZ, opening.MaxZ))
                    return true;
            }
        }
        return false;
    }

    private static XYZ WallPlanePoint(WallDescriptor wall, RebarFace face, double depth)
    {
        var outerFace = wall.Center + wall.Outward * (wall.Thickness / 2);
        var innerFace = wall.Center - wall.Outward * (wall.Thickness / 2);
        return face == RebarFace.Outer ? outerFace - wall.Outward * depth : innerFace + wall.Outward * depth;
    }

    private static XYZ PointOnWall(WallDescriptor wall, XYZ planePoint, double along, double z)
    {
        var delta = along - planePoint.DotProduct(wall.Tangent);
        var point = planePoint + wall.Tangent * delta;
        return new XYZ(point.X, point.Y, z);
    }

    private static IReadOnlyList<(double Start, double End)> ClipInterval(double min, double max,
        IEnumerable<(double Start, double End)> exclusions, double minimumLength)
        => GeometryKernel.ClipInterval(min, max, exclusions, minimumLength);

    public static IReadOnlyList<double> EvenPositions(double min, double max, double maximumSpacing)
        => GeometryKernel.EvenPositions(min, max, maximumSpacing);

    private static int CountPositions(double length, double maximumSpacing) => GeometryKernel.CountPositions(length, maximumSpacing);

    private static bool TryIntersectLines(XYZ p1, XYZ d1, XYZ p2, XYZ d2, out XYZ intersection)
    {
        var denominator = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denominator) < 1e-9) { intersection = XYZ.Zero; return false; }
        var delta = p2 - p1;
        var t = (delta.X * d2.Y - delta.Y * d2.X) / denominator;
        intersection = p1 + d1 * t;
        return true;
    }

    private static RebarTypeInfo? ResolveType(RebarTypeCatalog catalog, string name, string code, ICollection<ValidationIssue> issues)
    {
        if (catalog.TryGet(name, out var info)) return info;
        issues.Add(new ValidationIssue(code, ValidationSeverity.Error, $"Không tìm thấy RebarBarType '{name}' trong model."));
        return null;
    }

    public static IReadOnlyList<ValidationIssue> ValidatePreset(ManholeRebarPreset preset)
    {
        var issues = new List<ValidationIssue>();
        if (preset.CoverMm <= 0 || preset.SlabSpacingMm <= 0 || preset.WallVerticalSpacingMm <= 0 ||
            preset.WallHorizontalSpacingMm <= 0 || preset.MinRetainedLengthMm <= 0 || preset.CornerLegMm <= 0)
            issues.Add(new ValidationIssue("PRESET_NUMERIC_INVALID", ValidationSeverity.Error,
                "Cover, spacing, chiều dài giữ lại và chiều dài neo góc phải lớn hơn 0."));
        if (preset.OpeningClearanceMm < 0 || preset.EndClearanceMm < 0 || preset.LayerGapMm < 0)
            issues.Add(new ValidationIssue("PRESET_CLEARANCE_INVALID", ValidationSeverity.Error,
                "Opening clearance, end clearance và khoảng hở lớp không được âm."));
        if (preset.CornerBarCount < 0 || preset.OpeningEdgeBarCount < 0)
            issues.Add(new ValidationIssue("PRESET_COUNT_INVALID", ValidationSeverity.Error,
                "Số lượng thép góc và thép viền lỗ không được âm."));
        return issues;
    }

    private static string FaceCode(RebarFace face) => face switch
    {
        RebarFace.Inner => "IN",
        RebarFace.Outer => "OUT",
        RebarFace.Top => "TOP",
        RebarFace.Bottom => "BOT",
        _ => "NA"
    };

    private static string DirectionCode(RebarDirection direction) => direction == RebarDirection.Vertical ? "V" : "H";
    private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
    private static XYZ Horizontal(XYZ vector) => new(vector.X, vector.Y, 0);

    private static void ValidatePlanGeometry(RebarPlan plan)
    {
        var seen = new Dictionary<string, PlannedBar>();
        foreach (var bar in plan.Bars)
        foreach (var segment in PlannedBarGeometry.GetSegments(bar))
        {
            var duplicateKey = SegmentKey(bar.HostId, segment.Start, segment.End);
            if (seen.TryGetValue(duplicateKey, out var previous) && previous.Key != bar.Key)
                plan.Issues.Add(new ValidationIssue("DUPLICATE_PLANNED_BAR", ValidationSeverity.Error,
                    $"Plan có centerline trùng giữa {previous.Key} và {bar.Key}.", bar.HostId));
            else seen[duplicateKey] = bar;

            if (bar.IsOpeningBar) continue;
            foreach (var opening in plan.Assembly.Openings)
            {
                if (opening.WallNumber > 0)
                {
                    // Corner L-bars are hosted on wall N but span wall N and N+1.
                    if (!WallBarMatchesOpening(bar, opening.WallNumber)) continue;
                    var wall = plan.Assembly.Walls.Single(value => value.Number == opening.WallNumber);
                    var a = (X: segment.Start.DotProduct(wall.Tangent), Y: segment.Start.Z);
                    var b = (X: segment.End.DotProduct(wall.Tangent), Y: segment.End.Z);
                    if (!IntersectsOpenRectangle(a, b, opening.MinAlong, opening.MaxAlong, opening.MinZ, opening.MaxZ)) continue;
                }
                else
                {
                    if (bar.Component != opening.Component) continue;
                    var a = (X: plan.Assembly.Frame.ToU(segment.Start), Y: plan.Assembly.Frame.ToV(segment.Start));
                    var b = (X: plan.Assembly.Frame.ToU(segment.End), Y: plan.Assembly.Frame.ToV(segment.End));
                    if (!IntersectsOpenRectangle(a, b, opening.MinU, opening.MaxU, opening.MinV, opening.MaxV)) continue;
                }
                plan.Issues.Add(new ValidationIssue("OPENING_ENVELOPE_COLLISION", ValidationSeverity.Error,
                    $"Centerline {bar.Key} giao OpeningEnvelope {opening.Key}.", bar.HostId, opening.SourceId));
            }
        }
    }

    private static bool IntersectsOpenRectangle((double X, double Y) a, (double X, double Y) b,
        double minX, double maxX, double minY, double maxY)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var t0 = 0d;
        var t1 = 1d;
        if (!Clip(-dx, a.X - minX, ref t0, ref t1) || !Clip(dx, maxX - a.X, ref t0, ref t1) ||
            !Clip(-dy, a.Y - minY, ref t0, ref t1) || !Clip(dy, maxY - a.Y, ref t0, ref t1)) return false;
        if (t1 - t0 < 1e-9) return false;
        var t = (t0 + t1) / 2;
        var x = a.X + dx * t;
        var y = a.Y + dy * t;
        const double epsilon = 1e-7;
        return x > minX + epsilon && x < maxX - epsilon && y > minY + epsilon && y < maxY - epsilon;
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static string SegmentKey(ElementId hostId, XYZ a, XYZ b)
    {
        static string Point(XYZ value) => $"{Math.Round(value.X, 6)}:{Math.Round(value.Y, 6)}:{Math.Round(value.Z, 6)}";
        var first = Point(a);
        var second = Point(b);
        return first.CompareTo(second) <= 0 ? $"{hostId}-{first}-{second}" : $"{hostId}-{second}-{first}";
    }

    /// <summary>Regular wall bars match their wall; corner bars match both walls of the corner pair.</summary>
    internal static bool WallBarMatchesOpening(PlannedBar bar, int openingWallNumber)
    {
        if (bar.WallNumber == openingWallNumber) return true;
        if (bar.Direction != RebarDirection.Corner) return false;
        var next = bar.WallNumber == 4 ? 1 : bar.WallNumber + 1;
        return openingWallNumber == next;
    }
}

public static class PlannedBarGeometry
{
    public static IEnumerable<(XYZ Start, XYZ End)> GetSegments(PlannedBar bar)
    {
        var paths = new List<IReadOnlyList<XYZ>>();
        if (!bar.IsSet || bar.PlannedQuantity <= 1)
        {
            paths.Add(bar.Points);
        }
        else
        {
            var step = bar.SetLength / (bar.PlannedQuantity - 1);
            for (var index = 0; index < bar.PlannedQuantity; index++)
                paths.Add(bar.Points.Select(point => point + bar.PlaneNormal * (step * index)).ToArray());
        }
        foreach (var path in paths)
        for (var index = 0; index < path.Count - 1; index++)
            yield return (path[index], path[index + 1]);
    }
}
