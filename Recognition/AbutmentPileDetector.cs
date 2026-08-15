using Autodesk.Revit.DB;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

/// <summary>
/// Discovers pile FamilyInstances under the abutment host bbox for mat opening cuts.
/// Piles are separate instances (e.g. CKN); they are not voids in the Generic Model solid.
/// </summary>
public sealed class AbutmentPileDetector
{
    private readonly Document _document;

    public AbutmentPileDetector(Document document) => _document = document;

    public IReadOnlyList<AbutmentPileExclusion> Detect(
        IReadOnlyCollection<Element> hosts, AbutmentRebarPresetV1 preset)
    {
        var hostBoxes = hosts.Select(host => host.get_BoundingBox(null))
            .Where(box => box is not null).Cast<BoundingBoxXYZ>().ToList();
        if (hostBoxes.Count == 0 || preset.Topology.PileNameContains.Count == 0) return [];

        var expand = UnitUtils.ConvertToInternalUnits(
            preset.Topology.PileSearchExpansionMm, UnitTypeId.Millimeters);
        var hostMinZ = hostBoxes.Min(box => box.Min.Z);
        var hostMaxZ = hostBoxes.Max(box => box.Max.Z);
        var min = new XYZ(
            hostBoxes.Min(box => box.Min.X) - expand,
            hostBoxes.Min(box => box.Min.Y) - expand,
            hostMinZ - expand);
        var max = new XYZ(
            hostBoxes.Max(box => box.Max.X) + expand,
            hostBoxes.Max(box => box.Max.Y) + expand,
            hostMaxZ + expand);
        var outline = new Outline(min, max);
        var filter = new BoundingBoxIntersectsFilter(outline);
        var defaultDiameter = preset.Topology.PileDefaultDiameterMm;
        var clearance = preset.Topology.PileClearanceMm;
        var fallbackPileTop = hostMinZ + UnitUtils.ConvertToInternalUnits(
            preset.Topology.PileHeadDepthMm, UnitTypeId.Millimeters);
        var tokens = preset.Topology.PileNameContains;
        var hostIds = hosts.Select(host => host.Id).ToHashSet();

        var results = new List<AbutmentPileExclusion>();
        foreach (var instance in new FilteredElementCollector(_document)
                     .WherePasses(filter)
                     .OfClass(typeof(FamilyInstance))
                     .Cast<FamilyInstance>())
        {
            if (hostIds.Contains(instance.Id)) continue;
            var label = $"{instance.Symbol?.FamilyName} {instance.Name} {instance.Symbol?.Name}";
            if (!tokens.Any(token =>
                    label.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;

            var center = ResolveCenter(instance);
            if (center is null) continue;
            var pileBb = GeometryService.GetBounds(instance);
            var searchDepth = UnitUtils.ConvertToInternalUnits(
                preset.Topology.PileSearchDepthMm, UnitTypeId.Millimeters);
            // Keep long piles by their physical vertical envelope; insertion points may sit at the pile toe.
            if (pileBb is not null)
            {
                if (pileBb.Min.Z > hostMaxZ + expand ||
                    pileBb.Max.Z < hostMinZ - searchDepth)
                    continue;
            }
            else if (center.Z > hostMaxZ + expand || center.Z < hostMinZ - searchDepth)
            {
                continue;
            }

            if (!AbutmentPileEvidenceKernel.TryResolve(
                    ResolveCylinderDiameterMm(instance),
                    ResolveParameterDiameterMm(instance),
                    defaultDiameter,
                    AbutmentPileEvidenceKernel.DefaultDisagreementToleranceMm(defaultDiameter),
                    out var diameter,
                    out var diameterError))
                throw new InvalidOperationException(diameterError);

            var diameterMm = diameter.DiameterMm;
            var measuredDiameterMm =
                diameter.Evidence == AbutmentPileEvidenceKernel.DiameterEvidence.PresetDefault
                    ? (double?)null
                    : diameterMm;
            var diameterEvidence = diameter.Evidence.ToString();
            results.Add(new AbutmentPileExclusion
            {
                SourceId = instance.Id,
                Center = center,
                PileRadius = UnitUtils.ConvertToInternalUnits(
                    diameterMm / 2, UnitTypeId.Millimeters),
                DiameterDisagreesWithPreset = diameter.DisagreesWithPreset,
                DiameterNote = diameter.Note,
                MinZ = pileBb?.Min.Z ?? hostMinZ,
                MaxZ = pileBb?.Max.Z ?? fallbackPileTop,
                Clearance = UnitUtils.ConvertToInternalUnits(
                    clearance, UnitTypeId.Millimeters),
                DiameterMm = diameterMm,
                MeasuredDiameterMm = measuredDiameterMm,
                DiameterEvidence = diameterEvidence,
                VerticalEnvelopeEvidence = pileBb is null
                    ? "PresetPileHeadDepth"
                    : "InstanceBoundingBox",
                Name = label.Trim()
            });
        }

        // De-dupe nested pile components by plan center; separate real piles cannot be 200 mm apart.
        var unique = new List<AbutmentPileExclusion>();
        var coincidence = UnitUtils.ConvertToInternalUnits(
            preset.Topology.PileCoincidenceToleranceMm, UnitTypeId.Millimeters);
        foreach (var pile in results.OrderBy(item => ElementIdCompat.ToLong(item.SourceId)))
        {
            if (unique.Any(existing =>
                    Math.Sqrt(Math.Pow(existing.Center.X - pile.Center.X, 2)
                              + Math.Pow(existing.Center.Y - pile.Center.Y, 2)) < coincidence))
                continue;
            unique.Add(pile);
        }
        return unique;
    }

    private static XYZ? ResolveCenter(FamilyInstance instance)
    {
        if (instance.Location is LocationPoint lp) return lp.Point;
        var bb = instance.get_BoundingBox(null);
        return bb is null ? null : (bb.Min + bb.Max) / 2;
    }

    private static double? ResolveParameterDiameterMm(FamilyInstance instance)
    {
        foreach (var name in new[]
                 {
                     "Diameter", "DIAMETER", "Đường kính", "Duong kinh", "D_coc", "D Cọc", "D coc", "D", "Ø", "Dia"
                 })
        {
            var parameter = instance.LookupParameter(name) ?? instance.Symbol?.LookupParameter(name);
            if (parameter is not { StorageType: StorageType.Double, HasValue: true }
                || parameter.AsDouble() <= 1e-9)
                continue;
            return UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), UnitTypeId.Millimeters);
        }

        return null;
    }

    /// <summary>
    /// Measures the pile shaft from a real cylindrical face.
    /// <para>
    /// This deliberately replaces the old bounding-box guess. Revit hands back the transformed local
    /// AABB rather than a tight fit, so a pile placed on the skew reports a box far wider than the
    /// shaft: on this model three piles measured 1594,7 mm across while their volume (71,2508 m³)
    /// matched the other four exactly and matched a D1200 × 63 m cylinder to 0,0007%. The old
    /// "is it round" test compared dx against dy, which a rotated square box passes too.
    /// </para>
    /// <para>Returns the largest shaft found: a pile with a widened head is still that pile.</para>
    /// </summary>
    private static double? ResolveCylinderDiameterMm(FamilyInstance instance)
    {
        double? largest = null;
        foreach (var solid in GeometryService.GetSolids(instance))
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not CylindricalFace cylindrical) continue;
                // Only the shaft counts: a cylinder whose axis is not vertical is a fillet or a
                // detail, not the pile bore.
                var axis = cylindrical.Axis;
                if (axis is null || Math.Abs(axis.Normalize().Z) < 0.9) continue;

                var radius = cylindrical.get_Radius(0)?.GetLength() ?? 0;
                if (radius <= 1e-9) continue;
                var diameterMm = UnitUtils.ConvertFromInternalUnits(
                    radius * 2, UnitTypeId.Millimeters);
                if (largest is null || diameterMm > largest) largest = diameterMm;
            }
        }

        return largest;
    }
}
