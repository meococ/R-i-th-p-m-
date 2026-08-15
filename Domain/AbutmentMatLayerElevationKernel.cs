using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

/// <summary>
/// Where one footing mat layer ended up: the bar centroid measured from the concrete face the
/// layer is covered against, plus the bar diameter that decides how much room it needs. The
/// drawing dimensions this centroid, not the clear cover, so it is the quantity worth asserting.
/// </summary>
public readonly record struct AbutmentMatLayerPlacement(
    string RuleId,
    AbutmentFaceRole FaceRole,
    double CentroidDepthMm,
    double BarDiameterMm);

/// <summary>Two mat layers of one face whose bars would share the same concrete.</summary>
public readonly record struct AbutmentMatLayerOverlap(
    string FirstRuleId,
    string SecondRuleId,
    double SeparationMm,
    double RequiredSeparationMm);

/// <summary>
/// Turns the declared face cover of a footing mat into the depth of its bar centroid, stacking
/// each inner layer clear of the layer already placed on the same face. Pure kernel: no Revit API,
/// no I/O, millimeters only.
/// </summary>
public static class AbutmentMatLayerElevationKernel
{
    /// <summary>
    /// Millimeters two centroids may fall short of the no-overlap minimum and still pass. Placement
    /// and readback both work to millimeter tolerances, so a tighter gate would report noise.
    /// </summary>
    public const double SeparationToleranceMm = 0.5;

    /// <summary>
    /// Depth of the bar centroid below the covered face: the clear cover plus one bar radius, and
    /// never closer to the face than the layer already placed on that face allows. The stacked
    /// requirement is the previous centroid plus both radii plus the declared clear gap, so a
    /// cover that would drive an inner layer into the outer one is raised instead of silently
    /// crossing it. Pass <paramref name="previousCentroidDepthMm"/> and
    /// <paramref name="previousBarDiameterMm"/> as null for the outermost layer of a face.
    /// </summary>
    public static bool TryResolveCentroidDepthFromFace(
        double faceCoverMm,
        double barDiameterMm,
        double? previousCentroidDepthMm,
        double? previousBarDiameterMm,
        double clearGapToPreviousMm,
        out double centroidDepthMm,
        out string error)
    {
        centroidDepthMm = 0;
        error = string.Empty;
        if (!double.IsFinite(faceCoverMm) || faceCoverMm <= 0)
        {
            error = $"ABUTMENT_MAT_LAYER_COVER_INVALID: lớp bảo vệ mặt {faceCoverMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_MAT_LAYER_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(clearGapToPreviousMm) || clearGapToPreviousMm < 0)
        {
            error = $"ABUTMENT_MAT_LAYER_CLEAR_GAP_INVALID: khoảng hở tới lớp ngoài " +
                    $"{clearGapToPreviousMm} mm phải hữu hạn và không âm.";
            return false;
        }
        if (previousCentroidDepthMm is null != previousBarDiameterMm is null)
        {
            error = "ABUTMENT_MAT_LAYER_PREVIOUS_INCOMPLETE: lớp ngoài phải khai đủ cả cao độ tim " +
                    "và đường kính, hoặc bỏ trống cả hai khi đây là lớp ngoài cùng.";
            return false;
        }

        var faceCentroidDepthMm = faceCoverMm + barDiameterMm / 2;
        if (previousCentroidDepthMm is not { } previousDepthMm ||
            previousBarDiameterMm is not { } previousDiameterMm)
        {
            centroidDepthMm = faceCentroidDepthMm;
            return true;
        }
        if (!double.IsFinite(previousDepthMm) || previousDepthMm <= 0 ||
            !double.IsFinite(previousDiameterMm) || previousDiameterMm <= 0)
        {
            error = $"ABUTMENT_MAT_LAYER_PREVIOUS_INVALID: lớp ngoài khai tim {previousDepthMm} mm " +
                    $"và đường kính {previousDiameterMm} mm; cả hai phải dương hữu hạn.";
            return false;
        }

        var stackedDepthMm = previousDepthMm + previousDiameterMm / 2 +
                             clearGapToPreviousMm + barDiameterMm / 2;
        centroidDepthMm = Math.Max(faceCentroidDepthMm, stackedDepthMm);
        return true;
    }

    /// <summary>
    /// Reports every pair of mat layers on one face whose centroids sit closer together than the
    /// sum of their radii, so two crossing mats cannot be planned into the same concrete. Layers of
    /// different faces are never compared, and a face carrying a single layer has nothing to
    /// compare: this reports overlaps, it does not require a second layer to exist.
    /// </summary>
    public static IReadOnlyList<AbutmentMatLayerOverlap> FindOverlaps(
        IReadOnlyList<AbutmentMatLayerPlacement> layers,
        double toleranceMm)
    {
        var overlaps = new List<AbutmentMatLayerOverlap>();
        if (layers is null || layers.Count < 2) return overlaps;
        var tolerance = double.IsFinite(toleranceMm) && toleranceMm > 0 ? toleranceMm : 0;
        for (var first = 0; first < layers.Count - 1; first++)
        for (var second = first + 1; second < layers.Count; second++)
        {
            var left = layers[first];
            var right = layers[second];
            if (left.FaceRole != right.FaceRole) continue;
            var required = (left.BarDiameterMm + right.BarDiameterMm) / 2;
            var separation = Math.Abs(left.CentroidDepthMm - right.CentroidDepthMm);
            if (separation + tolerance >= required) continue;
            overlaps.Add(new AbutmentMatLayerOverlap(
                left.RuleId, right.RuleId, separation, required));
        }
        return overlaps;
    }
}
