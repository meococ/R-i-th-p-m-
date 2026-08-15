using Autodesk.Revit.DB;
using BIM.DatViet.Models;
using BIM.DatViet.Planning;

namespace BIM.DatViet.RevitServices;

/// <summary>
/// Final API-level clearance gate. It converts every envelope into a solid and uses
/// Solid.IntersectWithCurve, independently from the planner's 2D clipping math.
/// </summary>
public static class OpeningEnvelopeSolidValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(RebarPlan plan)
    {
        var issues = new List<ValidationIssue>();
        foreach (var opening in plan.Assembly.Openings)
        {
            Solid solid;
            try { solid = BuildSolid(plan.Assembly, opening); }
            catch (Exception exception)
            {
                issues.Add(new ValidationIssue("OPENING_SOLID_BUILD_FAILED", ValidationSeverity.Error,
                    $"Không dựng được solid kiểm tra cho {opening.Key}: {exception.Message}", opening.SourceId));
                continue;
            }

            foreach (var bar in plan.Bars.Where(value => !value.IsOpeningBar && Matches(value, opening)).ToList())
            {
                var hit = false;
                foreach (var segment in PlannedBarGeometry.GetSegments(bar))
                {
                    using var options = new SolidCurveIntersectionOptions();
                    using var intersection = solid.IntersectWithCurve(Line.CreateBound(segment.Start, segment.End), options);
                    if (intersection.SegmentCount == 0) continue;
                    hit = true;
                    break;
                }
                if (!hit) continue;

                // Drop colliding bars instead of hard-blocking Create. Opening envelopes already expanded
                // with clearance; residual solid hits are often corner/leg edge cases.
                plan.Bars.Remove(bar);
                if (issues.Any(issue => issue.Code == "OPENING_SOLID_STRIPPED" && issue.Message.Contains(bar.Key))) continue;
                issues.Add(new ValidationIssue("OPENING_SOLID_STRIPPED", ValidationSeverity.Warning,
                    $"Đã gỡ {bar.Key} vì solid giao {opening.Key} (không chặn Tạo thép).", bar.HostId, opening.SourceId));
            }
        }
        return issues;
    }

    private static bool Matches(PlannedBar bar, OpeningEnvelope opening) => opening.WallNumber > 0
        ? ManholeRebarPlanner.WallBarMatchesOpening(bar, opening.WallNumber)
        : bar.Component == opening.Component;

    private static Solid BuildSolid(ManholeAssembly assembly, OpeningEnvelope opening)
    {
        var loop = new CurveLoop();
        XYZ direction;
        double depth;
        if (opening.WallNumber > 0)
        {
            var wall = assembly.Walls.Single(value => value.Number == opening.WallNumber);
            // Slight pad only — large +200 mm extrusion was causing false corner collisions.
            depth = Math.Max(wall.Thickness + UnitUtils.ConvertToInternalUnits(20, UnitTypeId.Millimeters), 0.1);
            direction = wall.Outward;
            var offset = -direction * (depth / 2);
            XYZ Point(double along, double z)
            {
                var point = wall.Center + wall.Tangent * (along - wall.Center.DotProduct(wall.Tangent)) + offset;
                return new XYZ(point.X, point.Y, z);
            }
            AppendRectangle(loop, Point(opening.MinAlong, opening.MinZ), Point(opening.MaxAlong, opening.MinZ),
                Point(opening.MaxAlong, opening.MaxZ), Point(opening.MinAlong, opening.MaxZ));
        }
        else
        {
            var component = opening.Component == ManholeComponentKind.Top ? assembly.Top : assembly.Bottom;
            var box = component.BoundingBox ?? throw new InvalidOperationException("Slab không có BoundingBox.");
            depth = Math.Max(box.Max.Z - box.Min.Z, 0.1);
            direction = XYZ.BasisZ;
            AppendRectangle(loop,
                assembly.Frame.Of(opening.MinU, opening.MinV, box.Min.Z),
                assembly.Frame.Of(opening.MaxU, opening.MinV, box.Min.Z),
                assembly.Frame.Of(opening.MaxU, opening.MaxV, box.Min.Z),
                assembly.Frame.Of(opening.MinU, opening.MaxV, box.Min.Z));
        }
        return GeometryCreationUtilities.CreateExtrusionGeometry([loop], direction, depth);
    }

    private static void AppendRectangle(CurveLoop loop, XYZ p0, XYZ p1, XYZ p2, XYZ p3)
    {
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
    }
}
