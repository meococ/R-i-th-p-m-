using Autodesk.Revit.DB;
using BIM.DatViet.Domain;

namespace BIM.DatViet.Models;

public enum AbutmentResolutionStatus
{
    Resolved,
    Ambiguous,
    Unsupported
}

public enum AbutmentWorkflowMode
{
    Create,
    RebuildZone
}

/// <summary>Presentation-only mode for the passive WPF review viewport. Never changes the Revit view.</summary>
public enum AbutmentViewportRenderMode
{
    XRay,
    Wireframe
}

public sealed class AbutmentLocalFrame
{
    public AbutmentLocalFrame(XYZ origin, XYZ longitudinal, XYZ transverse, XYZ vertical)
    {
        Origin = origin;
        Longitudinal = longitudinal.Normalize();
        Transverse = transverse.Normalize();
        Vertical = vertical.Normalize();
    }

    public XYZ Origin { get; }
    public XYZ Longitudinal { get; }
    public XYZ Transverse { get; }
    public XYZ Vertical { get; }
    public XYZ ToWorld(double longitudinal, double transverse, double vertical) =>
        Origin + Longitudinal * longitudinal + Transverse * transverse + Vertical * vertical;
    public XYZ ToLocal(XYZ point)
    {
        var offset = point - Origin;
        return new XYZ(offset.DotProduct(Longitudinal), offset.DotProduct(Transverse), offset.DotProduct(Vertical));
    }
}

public sealed class AbutmentLocalBox
{
    public required XYZ Min { get; init; }
    public required XYZ Max { get; init; }
    public double Length => Max.X - Min.X;
    public double Depth => Max.Y - Min.Y;
    public double Height => Max.Z - Min.Z;
    public XYZ Center => (Min + Max) / 2;
    public bool IsValid => Length > 1e-6 && Depth > 1e-6 && Height > 1e-6;
}

public sealed class AbutmentSolidDescriptor
{
    public required ElementId HostId { get; init; }
    public required Solid Solid { get; init; }
    public required AbutmentLocalBox LocalBox { get; init; }
    public required double Volume { get; init; }
}
public sealed class AbutmentPlanarFaceDescriptor
{
    public required ElementId HostId { get; init; }
    public required int SolidIndex { get; init; }
    public required int FaceIndex { get; init; }
    public required PlanarFace Face { get; init; }
    public required AbutmentLocalBox LocalBox { get; init; }
    public required XYZ Origin { get; init; }
    public required XYZ Normal { get; init; }
    /// <summary>Measured horizontal direction in the face plane.</summary>
    public required XYZ HorizontalDirection { get; init; }
    /// <summary>Measured face edge loops in world coordinates; first loop is the Revit outer loop.</summary>
    public required IReadOnlyList<IReadOnlyList<XYZ>> BoundaryLoops { get; init; }
    /// <summary>Cached local triangle vertices (three per triangle) for modeless WPF preview.</summary>
    public required IReadOnlyList<XYZ> PreviewTrianglesLocal { get; init; }
    public required double Area { get; init; }
}

public sealed class AbutmentZoneSurfacePair
{
    public required AbutmentPlanarFaceDescriptor FrontFace { get; init; }
    public required AbutmentPlanarFaceDescriptor BackFace { get; init; }
    /// <summary>Wing-only semantic face pointing toward the abutment core; null for non-wing zones.</summary>
    public AbutmentPlanarFaceDescriptor? InnerFace { get; init; }
    /// <summary>Wing-only semantic face pointing away from the abutment core; null for non-wing zones.</summary>
    public AbutmentPlanarFaceDescriptor? OuterFace { get; init; }
}


public sealed class AbutmentFootingBoundary
{
    /// <summary>Counter-clockwise convex footing soffit boundary in world XY.</summary>
    public required IReadOnlyList<XYZ> Vertices { get; init; }
    /// <summary>Direction parallel to the long skew edges (F1/F3).</summary>
    public required XYZ LongitudinalDirection { get; init; }
    /// <summary>Independent direction parallel to the end edges (F2/F4); not forced perpendicular.</summary>
    public required XYZ TransverseDirection { get; init; }
    public required double AcuteAngleDeg { get; init; }
    /// <summary>Physical long/end edge lengths in Revit internal units.</summary>
    public required double LongEdgeLength { get; init; }
    public required double EndEdgeLength { get; init; }
    public bool IsValid => Vertices.Count >= 3
                           && LongitudinalDirection.GetLength() > 0.9
                           && TransverseDirection.GetLength() > 0.9
                           && AcuteAngleDeg is > 5 and < 175;
}


public sealed class AbutmentZoneDescriptor
{
    public required AbutmentZoneKind Kind { get; init; }
    public required string Code { get; init; }
    public required AbutmentLocalBox LocalBox { get; init; }
    public List<int> SupportingSolidIndexes { get; } = [];
    public AbutmentZoneSurfacePair? SurfacePair { get; init; }
    /// <summary>Profile-authoritative station polygons used for canonical preview/planning.</summary>
    public AbutmentProfileZoneGeometry? ProfileGeometry { get; init; }
}

public sealed class AbutmentProfileZoneGeometry
{
    public required AbutmentZoneKind Kind { get; init; }
    public required IReadOnlyList<AbutmentProfileZoneSection> Sections { get; init; }
    public required string GeometryHash { get; init; }
}

public sealed class AbutmentProfileZoneSection
{
    public required AbutmentProfileMassKernel.ProfileStationRole Role { get; init; }
    public required double StationCoordinate { get; init; }
    public required IReadOnlyList<XYZ> OrderedPolygonLocal { get; init; }
}

public sealed class AbutmentHostContext
{
    public required Element Host { get; init; }
    public required IReadOnlyList<Element> ComponentHosts { get; init; }
    public required AbutmentLocalFrame Frame { get; init; }
    public required AbutmentLocalBox OverallBox { get; init; }
    public required IReadOnlyList<AbutmentSolidDescriptor> Solids { get; init; }
    public IReadOnlyList<AbutmentPlanarFaceDescriptor> PlanarFaces { get; init; } = [];
    public required string FamilyName { get; init; }
    public required string TypeName { get; init; }
    public required string GeometryHash { get; init; }
    public AbutmentFamilyDimensions Dimensions { get; init; } = new();
    /// <summary>Horizontal footprint of the profile-authoritative footing band in local frame.</summary>
    public AbutmentLocalBox? FootingFootprint { get; init; }
    /// <summary>Profile-authoritative footing plan polygon and its two non-orthogonal edge directions.</summary>
    public AbutmentFootingBoundary? FootingBoundary { get; init; }
    /// <summary>Measured host-solid footprint retained only for containment and mismatch validation.</summary>
    public AbutmentFootingBoundary? SolidFootingBoundary { get; init; }
    /// <summary>Resolved L/Center/R station-section contract used by footing planning.</summary>
    public AbutmentProfileFootingEvidence? ProfileFooting { get; init; }
    /// <summary>Validated ProfileTm14V1 partitions at the L/Center/R stations.</summary>
    public AbutmentCoreProfileEvidence? CoreProfile { get; init; }
    /// <summary>
    /// Validated LEFT/RIGHT inner/outer nested wing profiles. Their ordered ModelLine loops
    /// are geometry authority; host solids may only corroborate containment/mismatch.
    /// </summary>
    public IReadOnlyList<AbutmentWingProfileEvidence> WingProfiles { get; init; } = [];
    /// <summary>Stable hash of wing occurrence identity, placement, roles and ordered loops.</summary>
    public string WingProfileHash { get; init; } = string.Empty;
    /// <summary>Piles under footing used to cut mat bars (world XY cylinders with world-Z extents).</summary>
    public IReadOnlyList<AbutmentPileExclusion> Piles { get; init; } = [];
    /// <summary>
    /// Nested profile station sections (PROFILE_TM family) in abutment-local coordinates.
    /// Ordered loops are the canonical section/outline authority.
    /// </summary>
    public IReadOnlyList<AbutmentProfileMassEvidence> ProfileMasses { get; init; } = [];
    /// <summary>Stable hash of profile roles, ordered loops and occurrence placement.</summary>
    public string ProfileMassHash { get; init; } = string.Empty;
}

public sealed class AbutmentProfileMassEvidence
{
    public required long SourceElementIdValue { get; init; }
    public string OccurrenceKey { get; init; } = string.Empty;
    public string SymbolGeometryIdentifier { get; init; } = string.Empty;
    public string ParameterChecksum { get; init; } = string.Empty;
    public required string FamilyName { get; init; }
    public required string TypeName { get; init; }
    public AbutmentProfileMassKernel.ProfileStationRole Role { get; init; }
    public double StationCoordinate { get; init; }
    public required double OriginX { get; init; }
    public required double OriginY { get; init; }
    public required double OriginZ { get; init; }
    public required double MinX { get; init; }
    public required double MinY { get; init; }
    public required double MinZ { get; init; }
    public required double MaxX { get; init; }
    public required double MaxY { get; init; }
    public required double MaxZ { get; init; }
    public XYZ PlaneOriginLocal { get; init; } = XYZ.Zero;
    public XYZ PlaneBasisULocal { get; init; } = XYZ.BasisY;
    public XYZ PlaneBasisVLocal { get; init; } = XYZ.BasisZ;
    public XYZ PlaneNormalLocal { get; init; } = XYZ.BasisX;
    public IReadOnlyList<XYZ> OrderedLoopLocal { get; init; } = [];
    public double MaximumEndpointGap { get; init; }
    public double MaximumPlaneDeviation { get; init; }
    public string GeometryHash { get; init; } = string.Empty;
}

public sealed class AbutmentProfileFootingEvidence
{
    public required IReadOnlyList<XYZ> BoundaryVertices { get; init; }
    /// <summary>PROFILE-measured soffit. Receipt only; bars use <see cref="PlacementBottomLocalZ"/>.</summary>
    public required double BottomLocalZ { get; init; }
    /// <summary>PROFILE-measured footing top. Receipt only; bars use <see cref="PlacementTopLocalZ"/>.</summary>
    public required double TopLocalZ { get; init; }
    /// <summary>Solid soffit when measured, otherwise the PROFILE soffit.</summary>
    public required double PlacementBottomLocalZ { get; init; }
    /// <summary>Solid top when measured, otherwise the PROFILE top.</summary>
    public required double PlacementTopLocalZ { get; init; }
    public required double CenterInterpolationMismatch { get; init; }
    /// <summary>Plan (X/Y) mismatch only. Elevation is <see cref="ElevationMismatch"/>.</summary>
    public required double PlanMismatch { get; init; }
    public required double ElevationMismatch { get; init; }
    /// <summary>Kept as the plan mismatch so older diagnostics still read the gated quantity.</summary>
    public required double MaximumProfileToSolidMismatch { get; init; }
    public bool IsStraight { get; init; }
}

public sealed class AbutmentCoreProfileEvidence
{
    public required IReadOnlyList<AbutmentCoreProfileSectionEvidence> Sections { get; init; }
    public required string GeometryHash { get; init; }
    public required double MaximumCenterInterpolationMismatch { get; init; }
}

public sealed class AbutmentCoreProfileSectionEvidence
{
    public required AbutmentProfileMassKernel.ProfileStationRole Role { get; init; }
    public required double StationCoordinate { get; init; }
    public required IReadOnlyList<XYZ> FullProfileLocal { get; init; }
    public required IReadOnlyList<XYZ> FootingPolygonLocal { get; init; }
    public required IReadOnlyList<XYZ> StemPolygonLocal { get; init; }
    public required IReadOnlyList<XYZ> BackwallPolygonLocal { get; init; }
}

public sealed class AbutmentWingProfileEvidence
{
    public required long SourceElementIdValue { get; init; }
    public required string OccurrenceKey { get; init; }
    public string SymbolGeometryIdentifier { get; init; } = string.Empty;
    public string ParameterChecksum { get; init; } = string.Empty;
    public required string FamilyName { get; init; }
    public required string TypeName { get; init; }
    /// <summary>LEFT/RIGHT token evidence from the type; corroborates but never assigns geometry side.</summary>
    public required AbutmentWingProfileDeclaredSideKernel DeclaredSide { get; init; }
    public required AbutmentWingProfileSideKernel Side { get; init; }
    public required AbutmentWingProfileFaceRoleKernel FaceRole { get; init; }
    public required double CoreDirectionScore { get; init; }
    public required XYZ PlaneOriginLocal { get; init; }
    public required XYZ PlaneBasisULocal { get; init; }
    public required XYZ PlaneBasisVLocal { get; init; }
    public required XYZ PlaneNormalLocal { get; init; }
    public required IReadOnlyList<XYZ> OrderedLoopLocal { get; init; }
    public required double MaximumEndpointGap { get; init; }
    public required double MaximumPlaneDeviation { get; init; }
    public required string GeometryHash { get; init; }
}


public sealed class AbutmentPileExclusion
{
    public required ElementId SourceId { get; init; }
    public required XYZ Center { get; init; }
    /// <summary>Physical pile radius in Revit internal units.</summary>
    public required double PileRadius { get; init; }
    /// <summary>Physical pile vertical envelope in world-Z Revit internal units.</summary>
    public required double MinZ { get; init; }
    public required double MaxZ { get; init; }
    /// <summary>Required clear distance from pile surface to rebar surface, internal units.</summary>
    public required double Clearance { get; init; }
    /// <summary>Conservative diameter used for planning, in millimeters.</summary>
    public double DiameterMm { get; init; }
    /// <summary>Diameter measured from a cylindrical face or read from a family parameter; null means preset fallback.</summary>
    public double? MeasuredDiameterMm { get; init; }
    public string DiameterEvidence { get; init; } = string.Empty;
    /// <summary>
    /// True when the winning evidence and the preset disagree beyond tolerance. The exclusion zone
    /// is sized from the winning number, so a disagreement here decides how much steel gets cut
    /// around every pile.
    /// </summary>
    public bool DiameterDisagreesWithPreset { get; init; }
    /// <summary>Human-readable trail of which source won and what it was compared against.</summary>
    public string DiameterNote { get; init; } = string.Empty;
    public string VerticalEnvelopeEvidence { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>Measured family instance dimensions in millimeters (0 = unresolved).</summary>
public sealed class AbutmentFamilyDimensions
{
    public double FootingLengthMm { get; init; }
    public double FootingWidthMm { get; init; }
    public double StemTopThicknessMm { get; init; }
    public double StemBaseThicknessMm { get; init; }
    public double WingLengthMm { get; init; }
    public double WingLengthLeftMm { get; init; }
    public double WingLengthRightMm { get; init; }
    public double WingInnerLengthLeftMm { get; init; }
    public double WingInnerLengthRightMm { get; init; }
    public double WingOuterLengthLeftMm { get; init; }
    public double WingOuterLengthRightMm { get; init; }
    public double WingThicknessMm { get; init; }
    public double StemHeightLeftMm { get; init; }
    public double StemHeightRightMm { get; init; }
    public double SkewAngleDeg { get; init; }

    /// <summary>
    /// Whether a skew parameter was actually found. A family that simply has no skew parameter and
    /// a family that declares a square abutment both read 0 degrees, and only this flag separates
    /// them; without it a skewed abutment modelled without the parameter fails the boundary check.
    /// </summary>
    public bool SkewAngleDeclared { get; init; }
}

public sealed class AbutmentClassification
{
    public AbutmentResolutionStatus Status { get; set; }
    public AbutmentHostContext? Context { get; set; }
    public List<AbutmentZoneDescriptor> Zones { get; } = [];
    public List<ValidationIssue> Issues { get; } = [];
}

public sealed class PlannedAbutmentRebar
{
    public required string Key { get; init; }
    public required string ZoneCode { get; init; }
    public required AbutmentZoneKind Zone { get; init; }
    public required ElementId HostId { get; init; }
    public required AbutmentZoneRule Rule { get; init; }
    public required ElementId ResolvedBarTypeId { get; init; }
    public required string ResolvedBarTypeName { get; init; }
    public required IReadOnlyList<XYZ> Points { get; init; }
    public required XYZ PlaneNormal { get; init; }
    public AbutmentDistributionRepresentation DistributionRepresentation { get; init; }
    public double DistributionLength { get; init; }
    public double Spacing { get; init; }
    public int PlannedQuantity { get; init; } = 1;
    public double Cover { get; init; }
    public double CenterlineOffset { get; init; }
    /// <summary>Inward depth from the selected concrete face, including approved layer stacking.</summary>
    public double LayerDepth { get; init; }

    /// <summary>
    /// 1-based bar position this rebar belongs to. A position is what the drawing counts and what
    /// <c>expectedBarCount</c> means; a position longer than a stock bar is built from more than one
    /// rebar. Layers that never splice keep one piece per position, so this equals the running bar
    /// index they always had.
    /// </summary>
    public int PositionIndex { get; init; } = 1;

    /// <summary>1-based piece inside <see cref="PositionIndex"/>; always 1 when the position is one bar.</summary>
    public int PieceIndex { get; init; } = 1;

    /// <summary>Pieces the whole position is built from; 1 when the bar fits one stock bar.</summary>
    public int PieceCount { get; init; } = 1;

    /// <summary>Cut length of this piece in millimeters, measured from the planned centerline.</summary>
    public double PieceLengthMm { get; init; }

    /// <summary>Finished length of the whole bar position in millimeters, splice overlap removed.</summary>
    public double PositionLengthMm { get; init; }

    /// <summary>
    /// Lap the splice this piece takes part in actually achieves, in millimeters; 0 when the
    /// position carries no splice.
    /// </summary>
    public double LapLengthMm { get; init; }

    /// <summary>True when this position is laid reversed end for end so its splice falls in the second band.</summary>
    public bool ReversedLayout { get; init; }
}

public sealed class AbutmentRebarPlan
{
    public required AbutmentHostContext Context { get; init; }
    public required AbutmentRebarPresetV1 Preset { get; init; }
    public required string GeometryHash { get; init; }
    public string PlanHash { get; set; } = string.Empty;
    public List<AbutmentZoneDescriptor> Zones { get; } = [];
    public List<PlannedAbutmentRebar> Bars { get; } = [];
    public List<ValidationIssue> Issues { get; } = [];
    public bool CanCreate => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>
    /// Zone-scoped readiness: host-level errors still block every zone, but a failed Stem rule
    /// must not prevent Create on Footing when Footing bars are valid.
    /// </summary>
    public bool CanCreateZone(AbutmentZoneKind zone)
    {
        if (Bars.All(bar => bar.Zone != zone)) return false;
        foreach (var issue in Issues.Where(item => item.Severity == ValidationSeverity.Error))
        {
            if (IsHostLevelBlocker(issue.Code)) return false;
            if (IsRuleScopedGeometryOrTypeIssue(issue.Code))
            {
                // Block only when the message references a rule of this zone or no rule id is present.
                var zoneRuleIds = Bars.Where(bar => bar.Zone == zone)
                    .Select(bar => bar.Rule.RuleId)
                    .Concat(Preset.Rules.Where(rule => rule.Zone == zone && rule.Enabled).Select(rule => rule.RuleId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (zoneRuleIds.Count == 0) return false;
                if (zoneRuleIds.Any(id => issue.Message.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    return false;
                continue;
            }
            // Unknown error: fail closed for the zone.
            return false;
        }
        return true;
    }

    private static bool IsHostLevelBlocker(string code) =>
        code is "ABUTMENT_CANONICAL_ZONE_BIND_FAILED"
            or "REBAR_BAR_TYPE_CATALOG_EMPTY"
            or "ABUTMENT_REBAR_HOST_INVALID"
            or "REBAR_SHAPE_CATALOG_EMPTY"
            or "ABUTMENT_PLAN_BUILD_FAILED"
            or "ABUTMENT_FOOTING_TOP_UNRESOLVED"
            or "ABUTMENT_FOOTING_DIMENSIONS_AMBIGUOUS"
            or "ABUTMENT_LOCAL_FRAME_AMBIGUOUS"
            or "ABUTMENT_FOOTING_BOUNDARY_UNRESOLVED"
            or "ABUTMENT_PROFILE_FOOTING_INVALID"
            // Pile geometry is host-level, not rule-level: the exclusion envelope it feeds is read
            // by every zone that declares an obstacle policy. Listing it here states that on
            // purpose instead of relying on the unknown-code fallback below.
            or "ABUTMENT_PILE_DIAMETER_DISAGREES_WITH_PRESET"
            or "ABUTMENT_ZONE_NOT_UNIQUE";

    private static bool IsRuleScopedGeometryOrTypeIssue(string code) =>
        code is "ABUTMENT_BAR_GEOMETRY_UNRESOLVED"
            or "REBAR_BAR_TYPE_NOT_FOUND"
            or "REBAR_BAR_TYPE_DIAMETER_MISMATCH"
            or "REBAR_COVER_UNRESOLVED"
            or "ABUTMENT_RULE_ZONE_MISSING"
            or "ABUTMENT_MAT_LAYER_CLASH";
}

public sealed class ReviewedAbutmentPlanSnapshot
{
    public required AbutmentRebarPlan Plan { get; init; }
    public required AbutmentZoneKind Zone { get; init; }
    public required string HostUniqueId { get; init; }
    public required ElementId HostId { get; init; }
    public required string GeometryHash { get; init; }
    public required string RuleHash { get; init; }
    public required string PlanHash { get; init; }
    public required string SelectionZoneCode { get; init; }
    public required string SelectionFingerprint { get; init; }
    public required DateTime ReviewedUtc { get; init; }
}

public sealed class AbutmentRuleEdit
{
    public required string RuleId { get; init; }
    public bool Enabled { get; init; }
    public required string BarTypeName { get; init; }
    public double DiameterMm { get; init; }
    public double SpacingMm { get; init; }
    public int FixedNumber { get; init; }
    public double? CoverOverrideMm { get; init; }
    public int LayerOrder { get; init; }
    public double ClearGapToPreviousMm { get; init; }
    public double StartStationClearanceMm { get; init; }
    public double EndStationClearanceMm { get; init; }
}

public sealed class AbutmentCreatedRebarReceipt
{
    public required string Key { get; init; }
    public required string ZoneCode { get; init; }
    public required ElementId RebarId { get; init; }

    /// <summary>
    /// The very instance written into this bar's hidden <c>Piece</c> evidence, so the receipt and the
    /// model cannot state different lengths for the same object. Null only on a receipt written
    /// before an audit run resolved the measurements.
    /// </summary>
    public AbutmentBarPieceFacts? Piece { get; init; }
}

public sealed class AbutmentCreateReceipt
{
    public string Operation { get; set; } = string.Empty;
    public string GenerationId { get; set; } = string.Empty;
    public string DocumentPath { get; set; } = string.Empty;
    public string DocumentKey { get; set; } = string.Empty;
    public string HostUniqueId { get; set; } = string.Empty;
    public string RuleHash { get; set; } = string.Empty;
    public string GeometryHash { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public bool ReceiptPersisted { get; set; }
    public string ReceiptPath { get; set; } = string.Empty;
    public string ReceiptWriteError { get; set; } = string.Empty;
    public List<ElementId> CreatedIds { get; } = [];
    public List<ElementId> RemovedIds { get; } = [];
    public List<AbutmentCreatedRebarReceipt> CreatedItems { get; } = [];

    /// <summary>
    /// One row per layer actually built, so the receipt answers the quantity question on its own.
    /// Populated only after the bars are committed; a rolled-back run leaves it empty.
    /// </summary>
    public List<AbutmentReceiptLayerSummary> LayerSummary { get; } = [];

    /// <summary>Totals of <see cref="LayerSummary"/>; null when nothing was committed.</summary>
    public AbutmentReceiptTotals? SummaryTotals { get; set; }

    public List<ValidationIssue> Issues { get; } = [];
}
