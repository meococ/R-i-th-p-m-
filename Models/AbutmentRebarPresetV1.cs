using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BIM.DatViet.Domain;

namespace BIM.DatViet.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentZoneKind
{
    Footing,
    Stem,
    Backwall,
    WingwallLeft,
    WingwallRight,
    BearingSeat,
    ShearKey,
    Pedestal,
    PileHead
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentRuleSourceStatus
{
    Drawing,
    Standard,
    Inferred
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentSourceResolutionStatus
{
    Confirmed,
    Proposed,
    Unresolved
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentGeometryTemplate
{
    Unresolved,
    BottomLongitudinal,
    BottomTransverse,
    TopLongitudinal,
    TopTransverse,
    FootingVerticalConnector,
    VerticalFront,
    VerticalBack,
    HorizontalFront,
    HorizontalBack,
    VerticalInner,
    VerticalOuter,
    HorizontalInner,
    HorizontalOuter,
    StarterLongitudinal,
    StarterTransverse,
    ClosedTie,
    SurfaceFreeForm
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentShapeStrategy
{
    Unresolved,
    ShapeDriven,
    CurveDrivenFreeForm,
    CustomServerFreeForm
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentLayoutRule
{
    Unresolved,
    Single,
    MaximumSpacing,
    FixedNumber,
    StationSchedule
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentDistributionRepresentation
{
    ExplicitStations,
    NativeRebarSet
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentStationReferenceAxis
{
    /// <summary>Offsets are already measured perpendicular to the bar centerline.</summary>
    PerpendicularToBar,
    /// <summary>Offsets are source dimensions measured along the PROFILE footing long edge.</summary>
    LongitudinalBoundary,
    /// <summary>Offsets are source dimensions measured along the PROFILE footing end edge.</summary>
    TransverseBoundary
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentStationDatumEdge
{
    /// <summary>No approved datum edge; only a mirror-symmetric chain may be planned.</summary>
    Unresolved,
    /// <summary>Station 0 sits on the footing edge above the toe cantilever.</summary>
    ToeSide,
    /// <summary>Station 0 sits on the footing edge above the heel cantilever.</summary>
    HeelSide
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentFaceRole
{
    Unresolved,
    Bottom,
    Top,
    Front,
    Back,
    Inner,
    Outer,
    StartEnd,
    EndEnd,
    Opening
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentEndGeometryKind
{
    Unresolved,
    None,
    Hook,
    Crank,
    EndTreatment
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentDevelopmentStatus
{
    Unresolved,
    NoAdditionalLength,
    RequiredLength
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentObstaclePolicy
{
    Unresolved,
    Reject,
    ContinueThroughMonolithicHost,
    ApprovedDetail
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentContinuityKind
{
    Unresolved,
    Independent,
    Continuous,
    Dowel,
    Lap,
    Coupler
}

public sealed class AbutmentHookSpec
{
    public string TypeName { get; set; } = string.Empty;
    public double AngleDegrees { get; set; }
    public double LegLengthMm { get; set; }
    public double BendRadiusMm { get; set; }
    public string Orientation { get; set; } = string.Empty;
    public double RotationDegrees { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

public sealed class AbutmentEndGeometryIntent
{
    public AbutmentEndGeometryKind Kind { get; set; }
    public AbutmentHookSpec HookSpec { get; set; } = new();
}

public sealed class AbutmentDevelopmentSpec
{
    public AbutmentDevelopmentStatus Status { get; set; }
    public double RequiredLengthMm { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

public sealed class AbutmentContinuitySpec
{
    public string BarFamilyId { get; set; } = string.Empty;
    public AbutmentContinuityKind Kind { get; set; }
    public AbutmentZoneKind? TargetZone { get; set; }
    public bool Stagger { get; set; }
    public double MaximumSpliceFraction { get; set; } = 0.5;
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentSpliceRequirement
{
    /// <summary>Default value: a rule that never stated whether its bars splice must fail closed.</summary>
    Unresolved,
    /// <summary>Every bar of the rule fits one stock bar, so no splice exists.</summary>
    NotRequired,
    /// <summary>The bar is longer than a stock bar and has to be lapped.</summary>
    Required
}

/// <summary>How the drawing asks the splices of one layer to be distributed along the bars.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentSpliceStagger
{
    /// <summary>Default value: an unstated stagger rule must fail closed.</summary>
    Unresolved,
    /// <summary>Every bar spliced at the same station.</summary>
    None,
    /// <summary>Even bars laid reversed end for end, so their splice falls in the second band.</summary>
    ReversedEndForEnd
}

/// <summary>
/// Material and supply data the TCVN 11823-5:2017 kernels need before any anchorage, splice, bend or
/// cover length can be computed. Every field is nullable so the same class can carry the whole
/// preset's values at the root and a partial override on one rule; a value is only ever resolved by
/// <see cref="AbutmentRebarPresetV1.TryResolveStandardParameters"/>, never defaulted silently.
/// </summary>
public sealed class AbutmentStandardParametersSpec
{
    /// <summary>
    /// Specified compressive strength f'c in MPa used for the calculation, which is not always the
    /// concrete grade on the general-notes sheet: where the file states two grades the lower bound
    /// is the one that may be calculated on.
    /// </summary>
    public double? ConcreteStrengthMpa { get; set; }

    public string BarGrade { get; set; } = string.Empty;

    public double? YieldStrengthMpa { get; set; }

    public AbutmentExposureClass? ExposureClass { get; set; }

    /// <summary>
    /// Water-cement ratio of the mix. Never defaulted: §5.12.3 raises cover by 20% from a ratio of
    /// 0.50, so a silent assumption here understates cover.
    /// </summary>
    public double? WaterCementRatio { get; set; }

    public double? MaximumAggregateSizeMm { get; set; }

    /// <summary>Length of the stock bar the yard delivers, which decides whether a bar has to splice.</summary>
    public double? StockBarLengthMm { get; set; }

    /// <summary>
    /// Density of the reinforcing steel in kg/m³, the only quantity a bar bending schedule needs
    /// that no clause supplies. It lives here rather than in a table of masses per diameter because
    /// <see cref="AbutmentBarMassKernel"/> multiplies it by the nominal circle area, which answers
    /// for every diameter a rule can state instead of only the ones somebody tabulated.
    /// </summary>
    public double? SteelDensityKgPerM3 { get; set; }

    public string SourceEvidenceId { get; set; } = string.Empty;

    public string EngineerApprovalReference { get; set; } = string.Empty;

    /// <summary>Reading note: where each value comes from and which file conflict it resolves.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// One rule's standard parameters after the rule override has been laid over the preset values, with
/// every field present. Only reachable through
/// <see cref="AbutmentRebarPresetV1.TryResolveStandardParameters"/>, so an incomplete declaration can
/// never be read as a complete one.
/// </summary>
public sealed record AbutmentResolvedStandardParameters
{
    public required double ConcreteStrengthMpa { get; init; }
    public required string BarGrade { get; init; }
    public required double YieldStrengthMpa { get; init; }
    public required AbutmentExposureClass ExposureClass { get; init; }
    public required double WaterCementRatio { get; init; }
    public required double MaximumAggregateSizeMm { get; init; }
    public required double StockBarLengthMm { get; init; }
    public required double SteelDensityKgPerM3 { get; init; }
}

/// <summary>
/// The lap splice of one layer, stated the way the drawing states it: the table columns of
/// §5.11.5.3.1, the detailing module the yard rounds a lap onto, and the cutting policy the pair of
/// cut lengths is solved with.
///
/// The cut lengths themselves are deliberately absent. They follow from the bar length, which is
/// measured from the PROFILE boundary at plan time, so writing them here would freeze one bridge's
/// geometry into the pattern — the trap §36.1 keeps out of the station layout.
/// </summary>
public sealed class AbutmentLapSpliceSpec
{
    public AbutmentSpliceRequirement Requirement { get; set; }

    /// <summary>Column of the §5.11.5.3.1 table: share of the bars spliced inside one lap length.</summary>
    public AbutmentSplicedFractionBand SplicedFraction { get; set; }

    /// <summary>Class the table yields for <see cref="SplicedFraction"/>, kept so it is auditable.</summary>
    public AbutmentSpliceClass SpliceClass { get; set; }

    public AbutmentSpliceStagger Stagger { get; set; }

    /// <summary>
    /// Lap length to detail. Generated by
    /// <see cref="AbutmentDevelopmentLengthKernel.TryDetailedLapSpliceLength"/> and re-derived by
    /// <see cref="AbutmentRebarPresetV1.Validate"/>, so a hand-typed number cannot survive here.
    /// </summary>
    public double LapLengthMm { get; set; }

    /// <summary>Module the lap is rounded up onto, coarser than the 10 mm of the clause itself.</summary>
    public double LapDetailingModuleMm { get; set; }

    /// <summary>Module every cut length is rounded onto.</summary>
    public double CuttingModuleMm { get; set; }

    /// <summary>
    /// Share of the bar length the two splice-zone centres are separated by. Detailing policy, not a
    /// clause value, so it is declared instead of taken from a kernel default.
    /// </summary>
    public double SpliceCentreStaggerFraction { get; set; }

    public string SourceEvidenceId { get; set; } = string.Empty;

    public string EngineerApprovalReference { get; set; } = string.Empty;

    /// <summary>Reading note: which drawing note asks for the stagger and what the pattern solves to.</summary>
    public string Note { get; set; } = string.Empty;
}

public sealed class AbutmentCongestionPolicy
{
    public string ConflictClass { get; set; } = string.Empty;
    public int Priority { get; set; }
    public double MaximumShiftMm { get; set; }
    public bool AllowLayerShift { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

public sealed class AbutmentFootingCoverSpec
{
    public double? FaceMm { get; set; }
    public double? LongitudinalStartMm { get; set; }
    public double? LongitudinalEndMm { get; set; }
    public double? TransverseStartMm { get; set; }
    public double? TransverseEndMm { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentStationStepForm
{
    /// <summary>Default value: a step that never declared its form must fail closed.</summary>
    Unresolved,
    /// <summary>Evenly spaced group whose first bar sits at a declared offset from the anchor.</summary>
    Run,
    /// <summary>Evenly spaced group whose first station is the last station already placed.</summary>
    ContinuedRun,
    /// <summary>A single non-uniform offset from the last station to the next one.</summary>
    Jump
}

/// <summary>
/// One entry of the dimension chain exactly as the drawing writes it: either an evenly spaced
/// group or a single odd offset.
/// </summary>
public sealed class AbutmentStationStepSpec
{
    public AbutmentStationStepForm Kind { get; set; }
    /// <summary>First bar of a <see cref="AbutmentStationStepForm.Run"/>, measured from the anchor.</summary>
    public double? StartMm { get; set; }
    public double SpacingMm { get; set; }
    /// <summary>Number of even spaces in the group, not the number of bars.</summary>
    public int SpanCount { get; set; }
    /// <summary>Odd offset of a <see cref="AbutmentStationStepForm.Jump"/>, in millimeters.</summary>
    public double OffsetMm { get; set; }
}

/// <summary>
/// A band one layer deliberately leaves without bars. An unexplained gap cannot be told apart from
/// a lost bar, so the reason and the drawing address that authorise it are both mandatory.
/// </summary>
public sealed class AbutmentStationBlankSpec
{
    public double StartMm { get; set; }
    public double EndMm { get; set; }
    public string Reason { get; set; } = string.Empty;
    public AbutmentRuleSourceLocator? DrawingAddress { get; set; }
}

/// <summary>
/// The layer stated the way the drawing states it: anchored edge, the axis the dimension chain is
/// measured along, the run/jump chain and the bands left without bars. The band width is never
/// declared here; the planner measures it from the PROFILE boundary, so one bridge's skew can
/// never reach the layout description.
/// </summary>
public sealed class AbutmentStationLayoutSpec
{
    public AbutmentStationDatumEdge Anchor { get; set; } = AbutmentStationDatumEdge.Unresolved;
    public AbutmentStationReferenceAxis MeasuredAlong { get; set; } =
        AbutmentStationReferenceAxis.PerpendicularToBar;
    public List<AbutmentStationStepSpec> Steps { get; set; } = [];
    public List<AbutmentStationBlankSpec> Blanks { get; set; } = [];
    /// <summary>Reading note: what this chain reproduces and where the drawing states it.</summary>
    public string Note { get; set; } = string.Empty;
}

public sealed class AbutmentStationSchedule
{
    /// <summary>
    /// Bar-centerline offsets in millimeters from the minimum concrete-boundary projection,
    /// measured normal to the bar direction.
    /// </summary>
    public List<double> CenterlineOffsetsMm { get; set; } = [];
    /// <summary>
    /// Axis used by the drawing dimension chain. The planner projects this axis
    /// normal to the bar using the actual validated PROFILE parallelogram.
    /// </summary>
    public AbutmentStationReferenceAxis ReferenceAxis { get; set; } =
        AbutmentStationReferenceAxis.PerpendicularToBar;
    /// <summary>
    /// Semantic concrete edge that carries station 0. Measuring an asymmetric chain from the
    /// opposite edge mirrors every bar while the bar count and the plan-versus-readback check
    /// both stay clean, so the edge must be declared instead of inferred from a frame axis sign.
    /// </summary>
    public AbutmentStationDatumEdge DatumEdge { get; set; } = AbutmentStationDatumEdge.Unresolved;
    /// <summary>
    /// Optional drawing-shaped source of <see cref="CenterlineOffsetsMm"/>. While both are declared
    /// the planner keeps planning from the explicit offsets and only cross-checks them against the
    /// solved chain, so a rule that leaves this null behaves exactly as before.
    /// </summary>
    public AbutmentStationLayoutSpec? StationLayout { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
    public int ExpectedBarCount { get; set; }
    public string DrawingStationPack { get; set; } = string.Empty;
}

public sealed class AbutmentStandardProfile
{
    public string StandardId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool AuthorizedFullTextAvailable { get; set; }
    public string SourceReference { get; set; } = string.Empty;
}

public sealed class AbutmentSourceEvidence
{
    public string EvidenceId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Page { get; set; }
    public string DrawingTitle { get; set; } = string.Empty;
    public string DrawingCode { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string SuitabilityCode { get; set; } = string.Empty;
    public bool AuthorizedForCreate { get; set; }
    public string AuthorizationReference { get; set; } = string.Empty;
}

public sealed class AbutmentRuleSourceLocator
{
    public string DrawingCode { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Callout { get; set; } = string.Empty;
}

public sealed class AbutmentRebarShapeSignature
{
    public string RebarShapeName { get; set; } = string.Empty;
    public string RebarStyle { get; set; } = string.Empty;
    public int SegmentCount { get; set; }
    public string SegmentSignature { get; set; } = string.Empty;
    public bool UseHooksToMatchShape { get; set; }
    public bool CreateNewShape { get; set; }
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string EngineerApprovalReference { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentFootingBoundaryStrategy
{
    Parallelogram,
    ConvexHullClippedToSolid
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbutmentHostAssemblyStrategy
{
    SingleHost,
    RevitAssemblySeparateHosts
}

public sealed class AbutmentTopologyProfile
{
    public List<string> FamilyNameContains { get; set; } = [];
    public List<string> TypeNameContains { get; set; } = [];
    public List<string> ItemParameterNames { get; set; } = [];
    public List<string> ItemValueContains { get; set; } = [];
    public List<string> FootingLengthParameterNames { get; set; } = [];
    public List<string> FootingWidthParameterNames { get; set; } = [];
    public List<string> StemTopThicknessParameterNames { get; set; } = [];
    public List<string> StemBaseThicknessParameterNames { get; set; } = [];
    public List<string> WingLengthLeftParameterNames { get; set; } = [];
    public List<string> WingLengthRightParameterNames { get; set; } = [];
    public List<string> WingLengthParameterNames { get; set; } = [];
    /// <summary>Semantic checksum parameters for the inner wing profile; never replace measured face loops.</summary>
    public List<string> WingInnerLengthLeftParameterNames { get; set; } = [];
    public List<string> WingInnerLengthRightParameterNames { get; set; } = [];
    /// <summary>Semantic checksum parameters for the outer wing profile; never replace measured face loops.</summary>
    public List<string> WingOuterLengthLeftParameterNames { get; set; } = [];
    public List<string> WingOuterLengthRightParameterNames { get; set; } = [];
    public List<string> WingThicknessParameterNames { get; set; } = [];
    public List<string> StemHeightLeftParameterNames { get; set; } = [];
    public List<string> StemHeightRightParameterNames { get; set; } = [];
    public List<string> SkewAngleParameterNames { get; set; } = [];
    /// <summary>Nested Mass family name tokens used for automatic profile recognition (e.g. PROFILE_TM).</summary>
    public List<string> ProfileMassFamilyNameContains { get; set; } = [];
    /// <summary>Nested Mass type name tokens (e.g. PROFILE_TM, PROFILE_TM_L, PROFILE_TM_R). Empty = family tokens only.</summary>
    public List<string> ProfileMassTypeNameContains { get; set; } = [];
    /// <summary>When true, Analyze fails closed if no nested profile mass is found on the host.</summary>
    public bool RequireProfileMassMarkers { get; set; } = true;
    public double ProfileClosureToleranceMm { get; set; } = 1;
    public double ProfilePlanarityToleranceMm { get; set; } = 2;
    public double ProfileToSolidMismatchToleranceMm { get; set; } = 8;
    public double ProfileStationSeparationToleranceMm { get; set; } = 1;
    /// <summary>Nested wing-profile family tokens. The pilot evidence is TƯỜNG CÁNH - ADAPTIVE.</summary>
    public List<string> WingProfileFamilyNameContains { get; set; } = [];
    /// <summary>Type tokens corroborating a measured LEFT occurrence; never assign side by name.</summary>
    public List<string> WingProfileLeftTypeNameContains { get; set; } = [];
    /// <summary>Type tokens corroborating a measured RIGHT occurrence; never assign side by name.</summary>
    public List<string> WingProfileRightTypeNameContains { get; set; } = [];
    /// <summary>When true, unresolved four-profile wing topology blocks Analyze.</summary>
    public bool RequireWingProfileMarkers { get; set; }
    public double WingProfileStationSeparationToleranceMm { get; set; } = 1;
    public double WingProfileDirectionToleranceMm { get; set; } = 1;

    public AbutmentFootingBoundaryStrategy FootingBoundaryStrategy { get; set; }
    public AbutmentHostAssemblyStrategy HostAssemblyStrategy { get; set; }
    public double FootingDepthMm { get; set; }
    /// <summary>Allowed difference between expected and solid-derived footing depth (mm).</summary>
    public double FootingDepthToleranceMm { get; set; }
    /// <summary>Footing length along bridge axis (L_MO). 0 means no approved fallback.</summary>
    public double FootingLengthMm { get; set; }
    /// <summary>Footing width transverse (B_BE). 0 means no approved fallback.</summary>
    public double FootingWidthMm { get; set; }
    public double ToeWidthMm { get; set; }
    public double StemBaseThicknessMm { get; set; }
    public double HeelWidthMm { get; set; }
    public double StemThicknessMm { get; set; }
    public double BackwallHeightMm { get; set; }
    public double WingwallLengthMm { get; set; }
    public double WingwallThicknessMm { get; set; }
    public double BearingSeatHeightMm { get; set; }
    public double ShearKeyHeightMm { get; set; }
    public double PedestalHeightMm { get; set; }
    public double PileHeadDepthMm { get; set; }
    /// <summary>Approved fallback diameter when a pile exposes no reliable physical diameter.</summary>
    public double PileDefaultDiameterMm { get; set; }
    /// <summary>Required clear distance between pile surface and finished Rebar surface (mm).</summary>
    public double PileClearanceMm { get; set; }
    /// <summary>Additional planning allowance outside the required finished pile clearance (mm).</summary>
    public double PilePlacementMarginMm { get; set; }
    /// <summary>
    /// Maximum one-sided shortening that Revit may apply along a straight bar axis during
    /// shape normalization. Lateral movement and endpoint extension remain separately strict.
    /// </summary>
    public double RevitEndpointShorteningToleranceMm { get; set; }
    /// <summary>Minimum retained length for any footing-mat bar fragment after obstacle clipping (mm).</summary>
    public double MinimumMatPieceLengthMm { get; set; }
    public double PileSearchExpansionMm { get; set; }
    public double PileSearchDepthMm { get; set; }
    public double PileCoincidenceToleranceMm { get; set; }
    public List<string> PileNameContains { get; set; } = [];
    /// <summary>When false, missing evidence files become warnings instead of load failures.</summary>
    public bool RequireEvidenceFiles { get; set; } = true;
    public List<AbutmentZoneKind> RequiredZones { get; set; } = [];
}


public sealed class AbutmentZoneRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ZoneCode { get; set; } = string.Empty;
    public AbutmentZoneKind Zone { get; set; }
    public AbutmentGeometryTemplate GeometryTemplate { get; set; }
    public AbutmentShapeStrategy ShapeStrategy { get; set; } = AbutmentShapeStrategy.ShapeDriven;
    public AbutmentDistributionRepresentation DistributionRepresentation { get; set; } =
        AbutmentDistributionRepresentation.ExplicitStations;
    public AbutmentLayoutRule LayoutRule { get; set; } = AbutmentLayoutRule.MaximumSpacing;
    public AbutmentFaceRole FaceRole { get; set; }
    public string CanonicalMark { get; set; } = string.Empty;
    public string SourceDrawingMark { get; set; } = string.Empty;
    public string BarTypeName { get; set; } = string.Empty;
    public double DiameterMm { get; set; }
    public double SpacingMm { get; set; }
    public string RequiredBarGrade { get; set; } = string.Empty;
    public double MinimumYieldStrengthMpa { get; set; }
    public string BarGradeSourceEvidenceId { get; set; } = string.Empty;
    public string BarGradeEngineerApprovalReference { get; set; } = string.Empty;
    public string SpacingSourceEvidenceId { get; set; } = string.Empty;
    public string SpacingEngineerApprovalReference { get; set; } = string.Empty;
    public int FixedNumber { get; set; }
    public double? CoverOverrideMm { get; set; }
    public string CoverSourceEvidenceId { get; set; } = string.Empty;
    public string CoverEngineerApprovalReference { get; set; } = string.Empty;
    public AbutmentFootingCoverSpec FootingCover { get; set; } = new();
    public int LayerOrder { get; set; }
    public double ClearGapToPreviousMm { get; set; }
    public double StartStationClearanceMm { get; set; }
    public double EndStationClearanceMm { get; set; }
    public AbutmentStationSchedule StationSchedule { get; set; } = new();
    public AbutmentEndGeometryIntent StartEndGeometry { get; set; } = new();
    public AbutmentEndGeometryIntent EndEndGeometry { get; set; } = new();
    public AbutmentDevelopmentSpec Development { get; set; } = new();
    public AbutmentObstaclePolicy PileObstaclePolicy { get; set; }
    public AbutmentObstaclePolicy OpeningObstaclePolicy { get; set; }
    public string ObstacleEvidenceId { get; set; } = string.Empty;
    public bool ShapeVerified { get; set; }
    public string ShapeEvidenceId { get; set; } = string.Empty;
    public AbutmentRebarShapeSignature ShapeSignature { get; set; } = new();
    public string EngineerApprovalReference { get; set; } = string.Empty;
    public AbutmentContinuitySpec Continuity { get; set; } = new();
    /// <summary>
    /// Only what this rule genuinely differs on from
    /// <see cref="AbutmentRebarPresetV1.Standard"/>; null keeps the preset values unchanged.
    /// </summary>
    public AbutmentStandardParametersSpec? StandardOverride { get; set; }
    /// <summary>
    /// Lap splice of this layer. Null states nothing at all, which is why
    /// <see cref="AbutmentSpliceRequirement.NotRequired"/> exists separately: a bar proven to fit one
    /// stock bar is a checked fact, an absent block is not.
    /// </summary>
    public AbutmentLapSpliceSpec? LapSplice { get; set; }
    public List<AbutmentCongestionPolicy> CongestionPolicies { get; set; } = [];
    public bool IncludeFirst { get; set; } = true;
    public bool IncludeLast { get; set; } = true;
    /// <summary>
    /// Defaults to <c>false</c>: a rule that forgot to say anything must not place steel. The whole
    /// safety posture is "nothing runs until a drawing says it does", so an omitted field has to
    /// land on the closed side. Every rule in the packaged profile states this explicitly.
    /// </summary>
    public bool Enabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
    public AbutmentRuleSourceStatus SourceStatus { get; set; } = AbutmentRuleSourceStatus.Drawing;
    public AbutmentSourceResolutionStatus SourceResolutionStatus { get; set; } =
        AbutmentSourceResolutionStatus.Unresolved;
    public AbutmentRuleSourceLocator SourceLocator { get; set; } = new();
    public string SourceEvidenceId { get; set; } = string.Empty;
    public string StandardId { get; set; } = string.Empty;
    public string ClauseReference { get; set; } = string.Empty;
    public bool ProposedCorrection { get; set; }
    [JsonIgnore]
    public bool RequiresExistingRebarShape =>
        Enabled && ShapeStrategy == AbutmentShapeStrategy.ShapeDriven;

    /// <summary>
    /// Rebar objects one bar position is built from. A position longer than the yard's stock bar is
    /// lapped, and a single-splice pattern is two pieces. Derived instead of stored: the two cut
    /// lengths come from the bar length measured at plan time, so a stored count would be a second
    /// place the same fact could be wrong.
    /// </summary>
    [JsonIgnore]
    public int PiecesPerBarPosition =>
        LapSplice?.Requirement == AbutmentSpliceRequirement.Required ? 2 : 1;

    /// <summary>
    /// Rebar objects the whole rule produces. <see cref="AbutmentStationScheduleSpec.ExpectedBarCount"/>
    /// keeps its meaning — the number of bar positions the drawing states — because that is the
    /// number the source can be traced back to; this is the model count that follows from it.
    /// </summary>
    [JsonIgnore]
    public int ExpectedRebarObjectCount =>
        StationSchedule.ExpectedBarCount * PiecesPerBarPosition;
}

public sealed class AbutmentPresetIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool IsError { get; init; }
}

public sealed class AbutmentRebarPresetV1
{
    public string ProfileId { get; set; } = "DVB-ABUTMENT-UNCONFIGURED";
    public string RuleVersion { get; set; } = "2";
    public string SchemaVersion { get; set; } = "2.3";
    public string DisplayName { get; set; } = string.Empty;
    public string RuleHash { get; set; } = string.Empty;
    public AbutmentTopologyProfile Topology { get; set; } = new();
    public int MaxTemporaryPreviewRebars { get; set; }
    /// <summary>
    /// Material and supply data the standard kernels calculate on. It sits at the preset level
    /// because the concrete grade, the steel grade, the mix and the yard's stock length belong to
    /// the structure, not to one bar mark; a rule that genuinely differs states only what differs in
    /// <see cref="AbutmentZoneRule.StandardOverride"/>.
    /// </summary>
    public AbutmentStandardParametersSpec Standard { get; set; } = new();
    public List<AbutmentStandardProfile> StandardProfiles { get; set; } = [];
    public List<AbutmentSourceEvidence> SourceEvidence { get; set; } = [];
    public List<AbutmentZoneRule> Rules { get; set; } = [];
    [JsonIgnore]
    public List<AbutmentPresetIssue> EvidenceLoadIssues { get; set; } = [];
    [JsonIgnore]
    public int CatalogPriority { get; set; }
    [JsonIgnore]
    public DateTime CatalogTimestampUtc { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public bool UsesPilePlacementMargin =>
        Topology.PilePlacementMarginMm > 0 &&
        Rules.Any(rule =>
            rule.Enabled &&
            rule.Zone == AbutmentZoneKind.Footing &&
            rule.PileObstaclePolicy == AbutmentObstaclePolicy.Reject);

    public string ComputeHash()
    {
        var previous = RuleHash;
        RuleHash = string.Empty;
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                .Replace("-", string.Empty);
        }
        finally
        {
            RuleHash = previous;
        }
    }

    /// <summary>
    /// Lays a rule's <see cref="AbutmentZoneRule.StandardOverride"/> over <see cref="Standard"/> and
    /// only succeeds when every value a TCVN 11823-5:2017 kernel needs is present and inside the
    /// range the standard covers. A rule that has to compute anchorage, a splice or cover therefore
    /// cannot fall back on a number typed for one bridge: the missing field names itself instead.
    /// </summary>
    public bool TryResolveStandardParameters(
        AbutmentZoneRule rule,
        out AbutmentResolvedStandardParameters resolved,
        out string error)
    {
        resolved = EmptyStandardParameters;
        error = string.Empty;
        if (rule is null)
        {
            error = "ABUTMENT_STANDARD_RULE_UNDECLARED: chưa có rule để tra tham số tiêu chuẩn.";
            return false;
        }

        var over = rule.StandardOverride;
        var strength = over?.ConcreteStrengthMpa ?? Standard.ConcreteStrengthMpa;
        var grade = FirstNonBlank(over?.BarGrade, Standard.BarGrade);
        var yield = over?.YieldStrengthMpa ?? Standard.YieldStrengthMpa;
        var exposure = over?.ExposureClass ?? Standard.ExposureClass;
        var ratio = over?.WaterCementRatio ?? Standard.WaterCementRatio;
        var aggregate = over?.MaximumAggregateSizeMm ?? Standard.MaximumAggregateSizeMm;
        var stock = over?.StockBarLengthMm ?? Standard.StockBarLengthMm;
        var density = over?.SteelDensityKgPerM3 ?? Standard.SteelDensityKgPerM3;

        if (strength is not { } concreteStrength || !double.IsFinite(concreteStrength) ||
            concreteStrength < AbutmentDevelopmentLengthKernel.MinimumConcreteStrengthMPa ||
            concreteStrength > AbutmentDevelopmentLengthKernel.MaximumConcreteStrengthMPa)
        {
            error = "ABUTMENT_STANDARD_CONCRETE_STRENGTH_REQUIRED: standard.concreteStrengthMpa " +
                    $"phải nằm trong dải [{AbutmentDevelopmentLengthKernel.MinimumConcreteStrengthMPa:0.#}; " +
                    $"{AbutmentDevelopmentLengthKernel.MaximumConcreteStrengthMPa:0.#}] MPa; " +
                    $"giá trị hiện tại là {Describe(strength)}.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(grade))
        {
            error = "ABUTMENT_STANDARD_BAR_GRADE_REQUIRED: standard.barGrade chưa khai mác thép.";
            return false;
        }
        if (yield is not { } yieldStrength || !double.IsFinite(yieldStrength) ||
            yieldStrength < AbutmentDevelopmentLengthKernel.MinimumYieldStrengthMPa ||
            yieldStrength > AbutmentDevelopmentLengthKernel.MaximumYieldStrengthMPa)
        {
            error = "ABUTMENT_STANDARD_YIELD_STRENGTH_REQUIRED: standard.yieldStrengthMpa phải nằm " +
                    $"trong dải [{AbutmentDevelopmentLengthKernel.MinimumYieldStrengthMPa:0.#}; " +
                    $"{AbutmentDevelopmentLengthKernel.MaximumYieldStrengthMPa:0.#}] MPa; " +
                    $"giá trị hiện tại là {Describe(yield)}.";
            return false;
        }
        if (exposure is not { } exposureClass || exposureClass == AbutmentExposureClass.Unresolved)
        {
            error = "ABUTMENT_STANDARD_EXPOSURE_REQUIRED: standard.exposureClass chưa khai cấp phơi " +
                    "nhiễm nên không tra được lớp bảo vệ cơ bản của §5.12.3.";
            return false;
        }
        // The water-cement ratio deliberately has no fallback of its own: the cover kernel already
        // fails closed on a missing ratio, and this gate keeps that behaviour instead of letting a
        // preset-level default paper over it.
        if (ratio is not { } waterCementRatio || !double.IsFinite(waterCementRatio) ||
            waterCementRatio < AbutmentConcreteCoverKernel.MinimumWaterCementRatio ||
            waterCementRatio > AbutmentConcreteCoverKernel.MaximumWaterCementRatio)
        {
            error = "ABUTMENT_STANDARD_WATER_CEMENT_RATIO_REQUIRED: standard.waterCementRatio phải " +
                    $"nằm trong dải [{AbutmentConcreteCoverKernel.MinimumWaterCementRatio:0.00}; " +
                    $"{AbutmentConcreteCoverKernel.MaximumWaterCementRatio:0.00}] và không được mặc " +
                    $"định vì §5.12.3 nhân cover lên 1,2 từ tỷ lệ 0,50; giá trị hiện tại là {Describe(ratio)}.";
            return false;
        }
        if (aggregate is not { } aggregateSize || !double.IsFinite(aggregateSize) || aggregateSize <= 0)
        {
            error = "ABUTMENT_STANDARD_AGGREGATE_SIZE_REQUIRED: standard.maximumAggregateSizeMm phải " +
                    $"dương hữu hạn; giá trị hiện tại là {Describe(aggregate)}.";
            return false;
        }
        if (stock is not { } stockLength || !double.IsFinite(stockLength) || stockLength <= 0)
        {
            error = "ABUTMENT_STANDARD_STOCK_LENGTH_REQUIRED: standard.stockBarLengthMm phải dương " +
                    $"hữu hạn vì nó quyết định thanh có phải nối hay không; giá trị hiện tại là {Describe(stock)}.";
            return false;
        }
        if (density is not { } steelDensity || !double.IsFinite(steelDensity) ||
            steelDensity < AbutmentBarMassKernel.MinimumSteelDensityKgPerCubicMetre ||
            steelDensity > AbutmentBarMassKernel.MaximumSteelDensityKgPerCubicMetre)
        {
            error = "ABUTMENT_STANDARD_STEEL_DENSITY_REQUIRED: standard.steelDensityKgPerM3 phải nằm " +
                    $"trong dải [{AbutmentBarMassKernel.MinimumSteelDensityKgPerCubicMetre:0.#}; " +
                    $"{AbutmentBarMassKernel.MaximumSteelDensityKgPerCubicMetre:0.#}] kg/m³ vì cột khối " +
                    $"lượng của bảng thống kê tính từ nó; giá trị hiện tại là {Describe(density)}.";
            return false;
        }

        resolved = new AbutmentResolvedStandardParameters
        {
            ConcreteStrengthMpa = concreteStrength,
            BarGrade = grade,
            YieldStrengthMpa = yieldStrength,
            ExposureClass = exposureClass,
            WaterCementRatio = waterCementRatio,
            MaximumAggregateSizeMm = aggregateSize,
            StockBarLengthMm = stockLength,
            SteelDensityKgPerM3 = steelDensity
        };
        return true;
    }

    /// <summary>
    /// Re-derives the lap splice one rule declares from the standard, so the number in the preset is
    /// the kernel's answer and not a typed one. Fails closed on anything the kernels refuse.
    /// </summary>
    public bool TryResolveDeclaredLapSplice(
        AbutmentZoneRule rule,
        out AbutmentLapSplice splice,
        out string error)
    {
        splice = new AbutmentLapSplice();
        error = string.Empty;
        if (rule?.LapSplice is not { } spec)
        {
            error = "ABUTMENT_LAP_SPLICE_UNDECLARED: rule chưa khai khối lapSplice.";
            return false;
        }
        if (!TryResolveStandardParameters(rule, out var parameters, out error)) return false;

        return AbutmentDevelopmentLengthKernel.TryDetailedLapSpliceLength(
            new AbutmentRebarMaterial
            {
                BarDiameterMm = rule.DiameterMm,
                ConcreteStrengthMPa = parameters.ConcreteStrengthMpa,
                YieldStrengthMPa = parameters.YieldStrengthMpa
            },
            new AbutmentTensionDevelopmentModifiers(),
            new AbutmentLapSpliceRequest { SplicedFraction = spec.SplicedFraction },
            spec.LapDetailingModuleMm,
            out splice,
            out error);
    }

    public AbutmentRebarPresetV1 Clone()
    {
        var clone = JsonSerializer.Deserialize<AbutmentRebarPresetV1>(
                        JsonSerializer.Serialize(this, JsonOptions), JsonOptions)
                    ?? throw new InvalidOperationException("Không clone được preset.");
        clone.CatalogPriority = CatalogPriority;
        clone.CatalogTimestampUtc = CatalogTimestampUtc;
        clone.EvidenceLoadIssues = EvidenceLoadIssues
            .Select(issue => new AbutmentPresetIssue
            {
                Code = issue.Code,
                Message = issue.Message,
                IsError = issue.IsError
            })
            .ToList();
        return clone;
    }

    public IReadOnlyList<AbutmentPresetIssue> Validate()
    {
        var issues = new List<AbutmentPresetIssue>();
        void Error(string code, string message) => issues.Add(new AbutmentPresetIssue { Code = code, Message = message, IsError = true });
        void Warning(string code, string message) => issues.Add(new AbutmentPresetIssue { Code = code, Message = message });

        if (!SchemaVersion.Equals("2.3", StringComparison.Ordinal)) Error("ABUTMENT_PRESET_SCHEMA_UNSUPPORTED", $"Schema '{SchemaVersion}' không được hỗ trợ.");
        if (string.IsNullOrWhiteSpace(ProfileId)) Error("ABUTMENT_PROFILE_ID_REQUIRED", "Thiếu profileId.");
        if (Rules.Count == 0) Error("ABUTMENT_RULES_EMPTY", "Preset chưa có rule thép.");
        else if (Rules.All(rule => !rule.Enabled))
            Warning("ABUTMENT_ENABLED_RULES_EMPTY",
                "Pilot cũ đã thu hồi do mapping hồ sơ cần hiệu chỉnh.");
        if (Topology.FamilyNameContains.Count == 0 &&
            (Topology.ItemParameterNames.Count == 0 || Topology.ItemValueContains.Count == 0))
            Error("ABUTMENT_PROFILE_IDENTITY_EMPTY",
                "Profile phải khóa family hoặc cặp itemParameterNames/itemValueContains.");
        if (MaxTemporaryPreviewRebars <= 0)
            Error("ABUTMENT_PREVIEW_LIMIT_INVALID", "maxTemporaryPreviewRebars phải > 0.");
        if (Topology.FootingDepthMm <= 0)
            Error("ABUTMENT_FOOTING_DEPTH_INVALID", "footingDepthMm phải > 0.");
        if (Topology.FootingDepthToleranceMm <= 0)
            Error("ABUTMENT_FOOTING_DEPTH_TOLERANCE_INVALID", "footingDepthToleranceMm phải > 0.");
        if (Topology.RequireProfileMassMarkers &&
            (Topology.ProfileClosureToleranceMm <= 0 ||
             Topology.ProfilePlanarityToleranceMm <= 0 ||
             Topology.ProfileToSolidMismatchToleranceMm <= 0 ||
             Topology.ProfileStationSeparationToleranceMm <= 0))
            Error("ABUTMENT_PROFILE_TOLERANCE_INVALID",
                "Các tolerance closure/planarity/profile-solid/station của profile phải > 0.");
        var wingProfileConfigured = Topology.WingProfileFamilyNameContains.Count > 0 ||
                                    Topology.WingProfileLeftTypeNameContains.Count > 0 ||
                                    Topology.WingProfileRightTypeNameContains.Count > 0;
        if (Topology.RequireWingProfileMarkers && !wingProfileConfigured)
            Error("ABUTMENT_WING_PROFILE_IDENTITY_REQUIRED",
                "Wing profile bắt buộc nhưng chưa khai báo family/type identity.");
        if (wingProfileConfigured &&
            (Topology.WingProfileFamilyNameContains.Count == 0 ||
             Topology.WingProfileLeftTypeNameContains.Count == 0 ||
             Topology.WingProfileRightTypeNameContains.Count == 0))
            Error("ABUTMENT_WING_PROFILE_IDENTITY_INCOMPLETE",
                "Wing profile phải có family token và token type LEFT/RIGHT độc lập.");
        if (wingProfileConfigured &&
            (!double.IsFinite(Topology.WingProfileStationSeparationToleranceMm) ||
             Topology.WingProfileStationSeparationToleranceMm <= 0 ||
             !double.IsFinite(Topology.WingProfileDirectionToleranceMm) ||
             Topology.WingProfileDirectionToleranceMm <= 0))
            Error("ABUTMENT_WING_PROFILE_TOLERANCE_INVALID",
                "Wing profile station/direction tolerance phải là số hữu hạn > 0.");
        if (Topology.PileNameContains.Count > 0)
        {
            if (Topology.PileDefaultDiameterMm <= 0)
                Error("ABUTMENT_PILE_DIAMETER_INVALID", "pileDefaultDiameterMm phải > 0 khi profile nhận diện cọc.");
            if (Topology.PileHeadDepthMm <= 0)
                Error("ABUTMENT_PILE_HEAD_DEPTH_INVALID", "pileHeadDepthMm phải > 0 khi profile nhận diện cọc.");
            if (Topology.PileClearanceMm <= 0)
                Error("ABUTMENT_PILE_CLEARANCE_INVALID", "pileClearanceMm phải > 0 khi profile nhận diện cọc.");
            if (Topology.PilePlacementMarginMm < 0)
                Error("ABUTMENT_PILE_PLACEMENT_MARGIN_INVALID",
                    "pilePlacementMarginMm không được âm khi profile nhận diện cọc.");
            if (Topology.PileSearchExpansionMm <= 0)
                Error("ABUTMENT_PILE_SEARCH_EXPANSION_INVALID",
                    "pileSearchExpansionMm phải > 0 khi profile nhận diện cọc.");
            if (Topology.PileSearchDepthMm <= 0 || Topology.PileCoincidenceToleranceMm <= 0)
                Error("ABUTMENT_PILE_SEARCH_TOLERANCE_INVALID",
                    "pileSearchDepthMm và pileCoincidenceToleranceMm phải > 0.");
        }
        if (Rules.Any(rule => rule.Enabled && rule.Zone == AbutmentZoneKind.Footing &&
                              IsFootingMatTemplate(rule.GeometryTemplate)) &&
            Topology.MinimumMatPieceLengthMm <= 0)
            Error("ABUTMENT_MINIMUM_MAT_PIECE_INVALID",
                "minimumMatPieceLengthMm phải > 0 khi có footing mat đang bật.");
        if (!double.IsFinite(Topology.RevitEndpointShorteningToleranceMm) ||
            Topology.RevitEndpointShorteningToleranceMm < 0 ||
            Topology.RevitEndpointShorteningToleranceMm > 50)
            Error("ABUTMENT_REVIT_ENDPOINT_SHORTENING_TOLERANCE_INVALID",
                "revitEndpointShorteningToleranceMm phải nằm trong khoảng 0..50 mm.");
        // A half-filled standard block is worse than an empty one: the missing half would be resolved
        // from nothing on the day a rule starts calculating. Once anything is stated, all of it is.
        var standardDeclared =
            Standard.ConcreteStrengthMpa is not null ||
            !string.IsNullOrWhiteSpace(Standard.BarGrade) ||
            Standard.YieldStrengthMpa is not null ||
            Standard.ExposureClass is not null ||
            Standard.WaterCementRatio is not null ||
            Standard.MaximumAggregateSizeMm is not null ||
            Standard.StockBarLengthMm is not null ||
            Standard.SteelDensityKgPerM3 is not null;
        if (standardDeclared &&
            !TryResolveStandardParameters(new AbutmentZoneRule(), out _, out var standardError))
            Error("ABUTMENT_STANDARD_PARAMETERS_INCOMPLETE",
                $"standard đã khai một phần nhưng {standardError}");
        if (standardDeclared &&
            string.IsNullOrWhiteSpace(Standard.SourceEvidenceId) &&
            string.IsNullOrWhiteSpace(Standard.EngineerApprovalReference))
            Error("ABUTMENT_STANDARD_SOURCE_REQUIRED",
                "standard thiếu evidence hoặc phê duyệt kỹ sư cho mác bê tông/thép, cấp phơi nhiễm, " +
                "tỷ lệ nước–xi măng, cỡ hạt cốt liệu, chiều dài phôi kho và khối lượng riêng thép.");


        foreach (var duplicate in Rules.GroupBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            Error("ABUTMENT_RULE_ID_DUPLICATE", $"RuleId '{duplicate.Key}' bị trùng.");
        foreach (var duplicate in Rules.Where(rule => !string.IsNullOrWhiteSpace(rule.ZoneCode))
                     .GroupBy(rule => rule.ZoneCode, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            Error("ABUTMENT_RULE_ZONE_CODE_DUPLICATE", $"ZoneCode '{duplicate.Key}' bị trùng.");
        foreach (var duplicate in Rules
                     .Where(rule => !string.IsNullOrWhiteSpace(rule.CanonicalMark))
                     .GroupBy(rule => new
                     {
                         rule.Zone,
                         Mark = rule.CanonicalMark.Trim().ToUpperInvariant()
                     })
                     .Where(group => group.Count() > 1))
            Error("ABUTMENT_RULE_CANONICAL_MARK_DUPLICATE",
                $"CanonicalMark '{duplicate.Key.Mark}' bị trùng trong zone {duplicate.Key.Zone}.");

        foreach (var rule in Rules.Where(rule => !rule.Enabled))
            Warning("ABUTMENT_DRAWING_RULE_DISABLED", $"{rule.RuleId}: {rule.DisabledReason}");

        var evidenceIds = SourceEvidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Standard.SourceEvidenceId) &&
            !evidenceIds.Contains(Standard.SourceEvidenceId))
            Error("ABUTMENT_STANDARD_SOURCE_INVALID", "evidence của standard không tồn tại.");
        var standards = StandardProfiles.ToDictionary(item => item.StandardId, StringComparer.OrdinalIgnoreCase);
        void ValidateEndGeometry(
            AbutmentZoneRule rule,
            AbutmentEndGeometryIntent intent,
            string endLabel)
        {
            if (intent.Kind is AbutmentEndGeometryKind.None or AbutmentEndGeometryKind.Unresolved)
                return;
            if (intent.Kind != AbutmentEndGeometryKind.Hook)
            {
                Error("ABUTMENT_RULE_END_GEOMETRY_UNSUPPORTED",
                    $"{rule.RuleId}: {endLabel} dùng {intent.Kind}; runtime hiện chỉ hỗ trợ None hoặc Hook.");
                return;
            }

            var hook = intent.HookSpec;
            if (string.IsNullOrWhiteSpace(hook.TypeName))
                Error("ABUTMENT_RULE_HOOK_TYPE_REQUIRED",
                    $"{rule.RuleId}: {endLabel} Hook thiếu RebarHookType.");
            if (!double.IsFinite(hook.AngleDegrees) || hook.AngleDegrees <= 0 || hook.AngleDegrees > 180)
                Error("ABUTMENT_RULE_HOOK_ANGLE_INVALID",
                    $"{rule.RuleId}: {endLabel} Hook phải có angle trong (0, 180]°.");
            if (!double.IsFinite(hook.LegLengthMm) || hook.LegLengthMm <= 0)
                Error("ABUTMENT_RULE_HOOK_LEG_INVALID",
                    $"{rule.RuleId}: {endLabel} Hook phải có legLengthMm > 0.");
            if (!double.IsFinite(hook.BendRadiusMm) || hook.BendRadiusMm <= 0)
                Error("ABUTMENT_RULE_HOOK_BEND_RADIUS_INVALID",
                    $"{rule.RuleId}: {endLabel} Hook phải có bendRadiusMm > 0.");
            if (!hook.Orientation.Equals("Left", StringComparison.OrdinalIgnoreCase) &&
                !hook.Orientation.Equals("Right", StringComparison.OrdinalIgnoreCase))
                Error("ABUTMENT_RULE_HOOK_ORIENTATION_INVALID",
                    $"{rule.RuleId}: {endLabel} Hook phải có orientation Left hoặc Right.");
            if (!double.IsFinite(hook.RotationDegrees) || Math.Abs(hook.RotationDegrees) > 360)
                Error("ABUTMENT_RULE_HOOK_ROTATION_INVALID",
                    $"{rule.RuleId}: {endLabel} Hook rotation phải hữu hạn trong [-360, 360]°.");
            if (string.IsNullOrWhiteSpace(hook.SourceEvidenceId) ||
                !evidenceIds.Contains(hook.SourceEvidenceId))
                Error("ABUTMENT_RULE_HOOK_EVIDENCE_MISSING",
                    $"{rule.RuleId}: {endLabel} Hook thiếu sourceEvidence hợp lệ.");
        }

        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleId) || string.IsNullOrWhiteSpace(rule.ZoneCode))
                Error("ABUTMENT_RULE_IDENTITY_REQUIRED", "Mỗi rule phải có ruleId và zoneCode.");
            if (string.IsNullOrWhiteSpace(rule.CanonicalMark))
                Error("ABUTMENT_RULE_CANONICAL_MARK_REQUIRED",
                    $"{rule.RuleId}: thiếu canonicalMark.");
            else if (!HasAllowedCanonicalPrefix(rule.CanonicalMark))
                Error("ABUTMENT_RULE_CANONICAL_PREFIX_INVALID",
                    $"{rule.RuleId}: canonicalMark '{rule.CanonicalMark}' không thuộc A/B/C/D/P/N/S/F.");
            else if (rule.Enabled && !CanonicalPrefixMatchesZone(rule))
                Error("ABUTMENT_RULE_CANONICAL_ZONE_MISMATCH",
                    $"{rule.RuleId}: canonicalMark '{rule.CanonicalMark}' không phù hợp zone {rule.Zone}.");
            if (rule.Enabled && rule.CanonicalMark.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                Error("ABUTMENT_RULE_OUT_OF_SCOPE",
                    $"{rule.RuleId}: canonical D thuộc Trụ/Xà mũ, ngoài scope command Mố.");
            if (string.IsNullOrWhiteSpace(rule.SourceDrawingMark))
                Error("ABUTMENT_RULE_SOURCE_DRAWING_MARK_REQUIRED",
                    $"{rule.RuleId}: thiếu sourceDrawingMark nguyên văn.");
            if (rule.Enabled &&
                (rule.SourceLocator.Page <= 0 ||
                 (string.IsNullOrWhiteSpace(rule.SourceLocator.Detail) &&
                  string.IsNullOrWhiteSpace(rule.SourceLocator.Section) &&
                  string.IsNullOrWhiteSpace(rule.SourceLocator.Note) &&
                  string.IsNullOrWhiteSpace(rule.SourceLocator.Callout))))
                Error("ABUTMENT_RULE_SOURCE_LOCATOR_REQUIRED",
                    $"{rule.RuleId}: enabled rule thiếu page và detail/section/note/callout.");
            if (rule.SourceStatus == AbutmentRuleSourceStatus.Drawing)
            {
                var evidence = SourceEvidence.FirstOrDefault(item =>
                    item.EvidenceId.Equals(rule.SourceEvidenceId, StringComparison.OrdinalIgnoreCase));
                if (evidence is null)
                    Error("DRAWING_SOURCE_EVIDENCE_MISSING", $"{rule.RuleId}: thiếu sourceEvidence hợp lệ.");
                else if (rule.Enabled &&
                         (!evidence.AuthorizedForCreate ||
                          string.IsNullOrWhiteSpace(evidence.Revision) ||
                          string.IsNullOrWhiteSpace(evidence.SuitabilityCode) ||
                          string.IsNullOrWhiteSpace(evidence.AuthorizationReference)))
                    Error("DRAWING_SOURCE_NOT_AUTHORIZED_FOR_CREATE",
                        $"{rule.RuleId}: evidence {evidence.EvidenceId} chưa có revision, suitability " +
                        "và phê duyệt cho phép Create.");
                if (string.IsNullOrWhiteSpace(rule.SourceDrawingMark))
                    Error("DRAWING_BAR_MARK_MISSING",
                        $"{rule.RuleId}: chưa có sourceDrawingMark từ bản vẽ.");
            }
            else if (string.IsNullOrWhiteSpace(rule.StandardId) ||
                     !standards.TryGetValue(rule.StandardId, out var standard) ||
                     !standard.AuthorizedFullTextAvailable ||
                     string.IsNullOrWhiteSpace(rule.ClauseReference))
            {
                Error("STANDARD_CLAUSE_SOURCE_MISSING",
                    $"{rule.RuleId}: rule {rule.SourceStatus} thiếu toàn văn/điều khoản tiêu chuẩn được phép sử dụng.");
            }
            if (rule.Enabled &&
                rule.SourceResolutionStatus != AbutmentSourceResolutionStatus.Confirmed)
                Error("ABUTMENT_RULE_SOURCE_UNRESOLVED",
                    $"{rule.RuleId}: source mapping phải Confirmed trước khi Create.");
        }

        foreach (var rule in Rules.Where(rule => rule.Enabled))
        {
            var footingSupported =
                rule.Zone == AbutmentZoneKind.Footing &&
                rule.GeometryTemplate is
                    (AbutmentGeometryTemplate.BottomLongitudinal or
                     AbutmentGeometryTemplate.BottomTransverse or
                     AbutmentGeometryTemplate.TopLongitudinal or
                     AbutmentGeometryTemplate.TopTransverse);
            var surfaceSupported =
                rule.Zone is
                    (AbutmentZoneKind.Stem or
                     AbutmentZoneKind.Backwall or
                     AbutmentZoneKind.WingwallLeft or
                     AbutmentZoneKind.WingwallRight) &&
                rule.GeometryTemplate is
                    (AbutmentGeometryTemplate.VerticalFront or
                     AbutmentGeometryTemplate.VerticalBack or
                     AbutmentGeometryTemplate.HorizontalFront or
                     AbutmentGeometryTemplate.HorizontalBack or
                     AbutmentGeometryTemplate.VerticalInner or
                     AbutmentGeometryTemplate.VerticalOuter or
                     AbutmentGeometryTemplate.HorizontalInner or
                     AbutmentGeometryTemplate.HorizontalOuter);
            // Thép chờ nối bệ với thân. Chỉ mở StarterLongitudinal đợt này: StarterTransverse chưa
            // có strategy runtime nên phải giữ ngoài whitelist. Bắt buộc khai continuity Dowel/Lap
            // trỏ về zone Stem — một thanh chờ không nói nó nối vào đâu thì không phải thanh chờ.
            var starterSupported =
                rule.Zone == AbutmentZoneKind.Footing &&
                rule.GeometryTemplate == AbutmentGeometryTemplate.StarterLongitudinal &&
                rule.FaceRole is (AbutmentFaceRole.Front or AbutmentFaceRole.Back) &&
                rule.Continuity.Kind is
                    (AbutmentContinuityKind.Dowel or AbutmentContinuityKind.Lap) &&
                rule.Continuity.TargetZone == AbutmentZoneKind.Stem;

            // Thép chờ rải theo bước tối đa dọc chân thân, không theo chuỗi ga đo từ bản vẽ.
            var layoutSupported = starterSupported
                ? rule.LayoutRule == AbutmentLayoutRule.MaximumSpacing
                : rule.LayoutRule == AbutmentLayoutRule.StationSchedule;
            var runtimeSupported =
                (footingSupported || surfaceSupported || starterSupported) &&
                layoutSupported &&
                (starterSupported ||
                 rule.DistributionRepresentation == AbutmentDistributionRepresentation.ExplicitStations);
            if (rule.ShapeStrategy == AbutmentShapeStrategy.CustomServerFreeForm)
                Error("ABUTMENT_RULE_RUNTIME_CAPABILITY_UNSUPPORTED",
                    $"{rule.RuleId}: CustomServerFreeForm chưa có server ổn định.");
            if (rule.ShapeStrategy == AbutmentShapeStrategy.CurveDrivenFreeForm &&
                (rule.StartEndGeometry.Kind != AbutmentEndGeometryKind.None ||
                 rule.EndEndGeometry.Kind != AbutmentEndGeometryKind.None))
                Error("ABUTMENT_RULE_FREEFORM_END_GEOMETRY_UNSUPPORTED",
                    $"{rule.RuleId}: CurveDrivenFreeForm hiện chỉ hỗ trợ thanh không hook/end treatment.");
            if (!runtimeSupported)
                Error("ABUTMENT_RULE_RUNTIME_CAPABILITY_UNSUPPORTED",
                    $"{rule.RuleId}: runtime production chỉ hỗ trợ Footing mat hoặc measured surface " +
                    "dùng StationSchedule dạng ExplicitStations, hoặc StarterLongitudinal dùng " +
                    "MaximumSpacing với continuity Dowel/Lap trỏ về Stem; các template khác phải " +
                    "giữ disabled.");
            if (rule.LayoutRule == AbutmentLayoutRule.MaximumSpacing && !starterSupported)
                Error("ABUTMENT_RULE_MAXIMUM_SPACING_FORBIDDEN",
                    $"{rule.RuleId}: production cấm MaximumSpacing ngoài thép chờ; phải dùng StationSchedule.");
            if (rule.DiameterMm <= 0) Error("ABUTMENT_RULE_DIAMETER_INVALID", $"{rule.RuleId}: diameter phải > 0.");
            if (string.IsNullOrWhiteSpace(rule.RequiredBarGrade) ||
                !double.IsFinite(rule.MinimumYieldStrengthMpa) ||
                rule.MinimumYieldStrengthMpa <= 0)
                Error("ABUTMENT_RULE_BAR_GRADE_REQUIRED",
                    $"{rule.RuleId}: phải khai báo mác thép và cường độ chảy tối thiểu.");
            if (string.IsNullOrWhiteSpace(rule.BarGradeSourceEvidenceId) &&
                string.IsNullOrWhiteSpace(rule.BarGradeEngineerApprovalReference))
                Error("ABUTMENT_RULE_BAR_GRADE_SOURCE_REQUIRED",
                    $"{rule.RuleId}: mác thép/cường độ chảy thiếu evidence hoặc phê duyệt kỹ sư.");
            else if (!string.IsNullOrWhiteSpace(rule.BarGradeSourceEvidenceId) &&
                     !evidenceIds.Contains(rule.BarGradeSourceEvidenceId))
                Error("ABUTMENT_RULE_BAR_GRADE_SOURCE_INVALID",
                    $"{rule.RuleId}: evidence mác thép không tồn tại.");
            if (rule.LayoutRule == AbutmentLayoutRule.MaximumSpacing)
            {
                if (rule.SpacingMm <= 0)
                    Error("ABUTMENT_RULE_SPACING_INVALID", $"{rule.RuleId}: spacing phải > 0.");
                if (string.IsNullOrWhiteSpace(rule.SpacingSourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(rule.SpacingEngineerApprovalReference))
                    Error("ABUTMENT_RULE_SPACING_SOURCE_REQUIRED",
                        $"{rule.RuleId}: spacing thiếu evidence hoặc phê duyệt kỹ sư.");
                else if (!string.IsNullOrWhiteSpace(rule.SpacingSourceEvidenceId) &&
                         !evidenceIds.Contains(rule.SpacingSourceEvidenceId))
                    Error("ABUTMENT_RULE_SPACING_SOURCE_INVALID",
                        $"{rule.RuleId}: evidence spacing không tồn tại.");
            }
            if (rule.LayoutRule == AbutmentLayoutRule.StationSchedule)
            {
                var offsets = rule.StationSchedule.CenterlineOffsetsMm;
                if (offsets.Count == 0 ||
                    offsets.Any(offset => !double.IsFinite(offset) || offset < 0) ||
                    offsets.Zip(offsets.Skip(1), (first, second) => second - first)
                        .Any(delta => delta <= 1e-9))
                    Error("ABUTMENT_RULE_STATION_SCHEDULE_INVALID",
                        $"{rule.RuleId}: stationSchedule phải có offset hữu hạn, không âm và tăng nghiêm ngặt.");
                if (rule.StationSchedule.ExpectedBarCount <= 0 ||
                    offsets.Count != rule.StationSchedule.ExpectedBarCount)
                    Error("ABUTMENT_RULE_STATION_COUNT_MISMATCH",
                        $"{rule.RuleId}: expectedBarCount phải dương và bằng số explicit station.");
                if (string.IsNullOrWhiteSpace(rule.StationSchedule.SourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(rule.StationSchedule.EngineerApprovalReference))
                    Error("ABUTMENT_RULE_STATION_SCHEDULE_SOURCE_REQUIRED",
                        $"{rule.RuleId}: stationSchedule thiếu evidence hoặc phê duyệt kỹ sư.");
                else if (!string.IsNullOrWhiteSpace(rule.StationSchedule.SourceEvidenceId) &&
                         !evidenceIds.Contains(rule.StationSchedule.SourceEvidenceId))
                    Error("ABUTMENT_RULE_STATION_SCHEDULE_SOURCE_INVALID",
                        $"{rule.RuleId}: evidence stationSchedule không tồn tại.");
                // A bare offset list is a set of numbers typed for one abutment. stationLayout is the
                // description the solver closes against the measured concrete band, and it is the only
                // thing that notices when the member it was written for changed size — the chain of
                // CVC-F5 adds up to exactly the 2000 mm the drawing states for the footing, so it
                // silently overruns the 1800 mm actually modelled.
                if (rule.StationSchedule.StationLayout is null)
                    Error("ABUTMENT_RULE_STATION_LAYOUT_REQUIRED",
                        $"{rule.RuleId}: rule đang bật dùng StationSchedule nên phải khai stationLayout; " +
                        "chuỗi ga thô là số gõ cho một mố, chỉ mô tả mới đóng chuỗi theo bề rộng bê tông đo được.");
            }
            // Only the footing mat resolves a PROFILE parallelogram, so it is the only path that can
            // measure the axes the layout is stated along. Anywhere else the description would be
            // carried without ever being checked, which is worse than not declaring it.
            if (rule.StationSchedule.StationLayout is not null && !footingSupported)
                Error("ABUTMENT_RULE_STATION_LAYOUT_UNSUPPORTED",
                    $"{rule.RuleId}: stationLayout hiện chỉ đối chiếu được cho lưới thép bệ; " +
                    "rule khác phải bỏ trường này thay vì mang một mô tả không ai kiểm.");
            if (rule.LayoutRule == AbutmentLayoutRule.FixedNumber && rule.FixedNumber < 1)
                Error("ABUTMENT_RULE_FIXED_NUMBER_INVALID", $"{rule.RuleId}: fixedNumber phải >= 1.");
            if (footingSupported)
            {
                var cover = rule.FootingCover;
                var values = new[]
                {
                    cover.FaceMm,
                    cover.LongitudinalStartMm,
                    cover.LongitudinalEndMm,
                    cover.TransverseStartMm,
                    cover.TransverseEndMm
                };
                if (values.Any(value => value is null || !double.IsFinite(value.Value) || value.Value <= 0))
                    Error("ABUTMENT_RULE_FOOTING_COVER_INCOMPLETE",
                        $"{rule.RuleId}: footingCover phải khai báo cover dương cho face và bốn cạnh.");
                if (string.IsNullOrWhiteSpace(cover.SourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(cover.EngineerApprovalReference))
                    Error("ABUTMENT_RULE_FOOTING_COVER_SOURCE_REQUIRED",
                        $"{rule.RuleId}: footingCover thiếu evidence hoặc phê duyệt kỹ sư.");
                else if (!string.IsNullOrWhiteSpace(cover.SourceEvidenceId) &&
                         !evidenceIds.Contains(cover.SourceEvidenceId))
                    Error("ABUTMENT_RULE_FOOTING_COVER_SOURCE_INVALID",
                        $"{rule.RuleId}: evidence footingCover không tồn tại.");
                if (rule.CoverOverrideMm is not null)
                    Error("ABUTMENT_RULE_FOOTING_LEGACY_COVER_FORBIDDEN",
                        $"{rule.RuleId}: footing mat không được dùng một coverOverride cho cả mặt và cạnh.");
            }
            else
            {
                if (rule.CoverOverrideMm is <= 0)
                    Error("ABUTMENT_RULE_COVER_INVALID", $"{rule.RuleId}: cover override phải > 0.");
                if (rule.CoverOverrideMm is > 0 &&
                    string.IsNullOrWhiteSpace(rule.CoverSourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(rule.CoverEngineerApprovalReference))
                    Error("ABUTMENT_RULE_COVER_SOURCE_REQUIRED",
                        $"{rule.RuleId}: cover override thiếu evidence hoặc phê duyệt kỹ sư.");
                else if (!string.IsNullOrWhiteSpace(rule.CoverSourceEvidenceId) &&
                         !evidenceIds.Contains(rule.CoverSourceEvidenceId))
                    Error("ABUTMENT_RULE_COVER_SOURCE_INVALID",
                        $"{rule.RuleId}: evidence cover không tồn tại.");
            }
            if (rule.LayerOrder < 0)
                Error("ABUTMENT_RULE_LAYER_ORDER_INVALID", $"{rule.RuleId}: layerOrder không được âm.");
            if (rule.ClearGapToPreviousMm < 0)
                Error("ABUTMENT_RULE_LAYER_GAP_INVALID", $"{rule.RuleId}: clearGapToPreviousMm không được âm.");
            if (rule.StartStationClearanceMm < 0 || rule.EndStationClearanceMm < 0)
                Error("ABUTMENT_RULE_STATION_CLEARANCE_INVALID",
                    $"{rule.RuleId}: khoảng lùi station đầu/cuối không được âm.");
            if (rule.StartEndGeometry.Kind == AbutmentEndGeometryKind.Unresolved ||
                rule.EndEndGeometry.Kind == AbutmentEndGeometryKind.Unresolved)
                Error("ABUTMENT_RULE_END_GEOMETRY_UNRESOLVED",
                    $"{rule.RuleId}: phải xác định rõ hình học hai đầu thanh; None cũng phải được khai báo.");
            ValidateEndGeometry(rule, rule.StartEndGeometry, "đầu");
            ValidateEndGeometry(rule, rule.EndEndGeometry, "cuối");
            if (string.IsNullOrWhiteSpace(rule.ShapeEvidenceId) ||
                !evidenceIds.Contains(rule.ShapeEvidenceId))
                Error("ABUTMENT_RULE_SHAPE_EVIDENCE_MISSING",
                    $"{rule.RuleId}: thiếu evidence cho shape/centerline.");
            if (!rule.ShapeVerified)
                Error("ABUTMENT_RULE_SHAPE_APPROVAL_REQUIRED",
                    $"{rule.RuleId}: shape/centerline chưa được kiểm chứng từ hồ sơ dự án.");
            if (rule.ShapeStrategy == AbutmentShapeStrategy.ShapeDriven)
            {
                var signature = rule.ShapeSignature;
                if (string.IsNullOrWhiteSpace(signature.RebarShapeName) ||
                    string.IsNullOrWhiteSpace(signature.RebarStyle) ||
                    signature.SegmentCount <= 0 ||
                    string.IsNullOrWhiteSpace(signature.SegmentSignature))
                    Error("ABUTMENT_RULE_SHAPE_SIGNATURE_REQUIRED",
                        $"{rule.RuleId}: ShapeDriven thiếu tên/style/count/signature của RebarShape đã duyệt.");
                if (!signature.RebarStyle.Equals("Standard", StringComparison.OrdinalIgnoreCase) &&
                    !signature.RebarStyle.Equals("StirrupTie", StringComparison.OrdinalIgnoreCase))
                    Error("ABUTMENT_RULE_SHAPE_STYLE_INVALID",
                        $"{rule.RuleId}: rebarStyle phải là Standard hoặc StirrupTie.");
                if (signature.UseHooksToMatchShape &&
                    rule.StartEndGeometry.Kind != AbutmentEndGeometryKind.Hook &&
                    rule.EndEndGeometry.Kind != AbutmentEndGeometryKind.Hook)
                    Error("ABUTMENT_RULE_SHAPE_HOOK_CONTRACT_INVALID",
                        $"{rule.RuleId}: useHooksToMatchShape yêu cầu ít nhất một đầu Hook.");
                if (signature.CreateNewShape)
                    Error("ABUTMENT_RULE_CREATE_NEW_SHAPE_FORBIDDEN",
                        $"{rule.RuleId}: production cấm Create New Shape.");
            }
            if (rule.Development.Status == AbutmentDevelopmentStatus.Unresolved)
                Error("ABUTMENT_RULE_DEVELOPMENT_UNRESOLVED",
                    $"{rule.RuleId}: chưa xác định yêu cầu chiều dài tăng thêm ở đầu thanh.");
            if (rule.Development.Status == AbutmentDevelopmentStatus.NoAdditionalLength &&
                Math.Abs(rule.Development.RequiredLengthMm) > 1e-9)
                Error("ABUTMENT_RULE_DEVELOPMENT_INVALID",
                    $"{rule.RuleId}: NoAdditionalLength phải có requiredLengthMm bằng 0.");
            if (rule.Development.Status == AbutmentDevelopmentStatus.NoAdditionalLength &&
                string.IsNullOrWhiteSpace(rule.Development.SourceEvidenceId) &&
                string.IsNullOrWhiteSpace(rule.Development.EngineerApprovalReference))
                Error("ABUTMENT_RULE_DEVELOPMENT_SOURCE_MISSING",
                    $"{rule.RuleId}: NoAdditionalLength thiếu evidence hoặc phê duyệt kỹ sư.");
            if (rule.Development.Status == AbutmentDevelopmentStatus.RequiredLength &&
                rule.Development.RequiredLengthMm <= 0)
                Error("ABUTMENT_RULE_DEVELOPMENT_INVALID",
                    $"{rule.RuleId}: RequiredLength phải có requiredLengthMm > 0.");
            if (rule.Development.RequiredLengthMm < 0)
                Error("ABUTMENT_RULE_DEVELOPMENT_INVALID",
                    $"{rule.RuleId}: chiều dài development không được âm.");
            if (rule.Development.Status == AbutmentDevelopmentStatus.RequiredLength &&
                string.IsNullOrWhiteSpace(rule.Development.SourceEvidenceId) &&
                string.IsNullOrWhiteSpace(rule.Development.EngineerApprovalReference))
                Error("ABUTMENT_RULE_DEVELOPMENT_SOURCE_MISSING",
                    $"{rule.RuleId}: development length thiếu evidence hoặc phê duyệt kỹ sư.");
            // The runtime still never lengthens a bar end by an anchorage length. The one ℓ_d-derived
            // length it does build is the lap of a declared splice, which the piece planner lays out
            // and the readback measures on the created bars. A development length consumed by that
            // splice is therefore supported; every other one is still a declaration with no runtime.
            if (rule.Development.Status == AbutmentDevelopmentStatus.RequiredLength &&
                rule.LapSplice?.Requirement != AbutmentSpliceRequirement.Required)
                Error("ABUTMENT_RULE_DEVELOPMENT_UNSUPPORTED",
                    $"{rule.RuleId}: phiên bản này chỉ dựng được chiều dài neo khi nó là nguồn của " +
                    "một lapSplice Required; chưa kéo dài được đầu thanh theo development length " +
                    "khai riêng.");
            if (rule.Continuity.Kind == AbutmentContinuityKind.Unresolved)
                Error("ABUTMENT_RULE_CONTINUITY_UNRESOLVED",
                    $"{rule.RuleId}: phải xác định continuity; Independent cũng phải được khai báo rõ.");
            else if (rule.Continuity.Kind == AbutmentContinuityKind.Independent)
            {
                if (!string.IsNullOrWhiteSpace(rule.Continuity.BarFamilyId) ||
                    rule.Continuity.TargetZone is not null)
                    Error("ABUTMENT_RULE_CONTINUITY_INDEPENDENT_INVALID",
                        $"{rule.RuleId}: Independent không được tham chiếu barFamilyId/targetZone.");
                if (string.IsNullOrWhiteSpace(rule.Continuity.SourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(rule.Continuity.EngineerApprovalReference))
                    Error("ABUTMENT_RULE_CONTINUITY_SOURCE_MISSING",
                        $"{rule.RuleId}: Independent thiếu evidence hoặc phê duyệt kỹ sư.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rule.Continuity.BarFamilyId) ||
                    rule.Continuity.TargetZone is null)
                    Error("ABUTMENT_RULE_CONTINUITY_LINK_REQUIRED",
                        $"{rule.RuleId}: continuity thiếu barFamilyId hoặc targetZone.");
                if (string.IsNullOrWhiteSpace(rule.Continuity.SourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(rule.Continuity.EngineerApprovalReference))
                    Error("ABUTMENT_RULE_CONTINUITY_SOURCE_MISSING",
                        $"{rule.RuleId}: continuity thiếu evidence hoặc phê duyệt kỹ sư.");
                if (rule.Continuity.Kind is AbutmentContinuityKind.Lap or AbutmentContinuityKind.Coupler &&
                    (rule.Continuity.MaximumSpliceFraction <= 0 ||
                     rule.Continuity.MaximumSpliceFraction > 0.5))
                    Error("ABUTMENT_RULE_CONTINUITY_SPLICE_FRACTION_INVALID",
                        $"{rule.RuleId}: maximumSpliceFraction phải trong khoảng (0; 0,5].");
                // A lap that stays inside the rule's own zone is exactly what the piece planner
                // builds: two overlapping pieces per bar position, odd and even positions laid
                // alternately end for end. A coupler, or a lap that hands the bar to another zone,
                // still has no runtime and must keep failing closed.
                if (rule.Continuity.Kind != AbutmentContinuityKind.Lap ||
                    rule.Continuity.TargetZone != rule.Zone ||
                    rule.LapSplice?.Requirement != AbutmentSpliceRequirement.Required)
                    Error("ABUTMENT_RULE_CONTINUITY_UNSUPPORTED",
                        $"{rule.RuleId}: phiên bản này chỉ dựng được continuity Lap trong cùng zone " +
                        "kèm lapSplice Required; continuity liên vùng và coupler chưa có runtime.");
            }
            foreach (var policy in rule.CongestionPolicies)
            {
                if (string.IsNullOrWhiteSpace(policy.ConflictClass) ||
                    policy.MaximumShiftMm < 0)
                    Error("ABUTMENT_RULE_CONGESTION_POLICY_INVALID",
                        $"{rule.RuleId}: congestion policy thiếu conflictClass hoặc maximumShiftMm không hợp lệ.");
                if (string.IsNullOrWhiteSpace(policy.SourceEvidenceId) &&
                    string.IsNullOrWhiteSpace(policy.EngineerApprovalReference))
                    Error("ABUTMENT_RULE_CONGESTION_SOURCE_MISSING",
                        $"{rule.RuleId}: congestion policy thiếu evidence hoặc phê duyệt kỹ sư.");
            }
            if (rule.CongestionPolicies.Count > 0)
                Error("ABUTMENT_RULE_CONGESTION_POLICY_UNSUPPORTED",
                    $"{rule.RuleId}: congestion solver chưa được triển khai; không được phép tự dịch thanh.");
            if (Topology.PileNameContains.Count > 0 &&
                rule.PileObstaclePolicy == AbutmentObstaclePolicy.Unresolved)
                Error("ABUTMENT_RULE_PILE_POLICY_UNRESOLVED",
                    $"{rule.RuleId}: chưa xác định policy khi thanh giao vùng đầu cọc.");
            if ((rule.PileObstaclePolicy is
                     (AbutmentObstaclePolicy.ContinueThroughMonolithicHost or
                      AbutmentObstaclePolicy.ApprovedDetail) ||
                 rule.OpeningObstaclePolicy is
                     (AbutmentObstaclePolicy.ContinueThroughMonolithicHost or
                      AbutmentObstaclePolicy.ApprovedDetail)) &&
                (string.IsNullOrWhiteSpace(rule.ObstacleEvidenceId) ||
                 !evidenceIds.Contains(rule.ObstacleEvidenceId)))
                Error("ABUTMENT_RULE_OBSTACLE_EVIDENCE_MISSING",
                    $"{rule.RuleId}: obstacle policy thiếu evidence hợp lệ.");
            if ((rule.PileObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail ||
                 rule.OpeningObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail) &&
                string.IsNullOrWhiteSpace(rule.EngineerApprovalReference))
                Error("ABUTMENT_RULE_OBSTACLE_APPROVAL_REQUIRED",
                    $"{rule.RuleId}: ApprovedDetail thiếu tham chiếu phê duyệt kỹ sư.");
            if (rule.PileObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail)
                Error("ABUTMENT_RULE_PILE_APPROVED_DETAIL_UNSUPPORTED",
                    $"{rule.RuleId}: runtime chưa dựng được detail né cọc có neo, continuity và bù thép.");
            if (rule.OpeningObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail)
                Error("ABUTMENT_RULE_OPENING_APPROVED_DETAIL_UNSUPPORTED",
                    $"{rule.RuleId}: runtime chưa dựng được detail qua opening có neo, continuity và bù thép.");
            // A rule that needs anchorage or a splice has to be able to compute them, and an enabled
            // rule has to be able to weigh the bars it creates, since every created bar carries a
            // mass onto the bar bending schedule. A frozen rule that needs neither is not asked for
            // parameters it does not use.
            if ((rule.Enabled ||
                 rule.Development.Status == AbutmentDevelopmentStatus.RequiredLength ||
                 rule.LapSplice?.Requirement == AbutmentSpliceRequirement.Required) &&
                !TryResolveStandardParameters(rule, out _, out var parameterError))
                Error("ABUTMENT_RULE_STANDARD_PARAMETERS_INCOMPLETE",
                    $"{rule.RuleId}: rule đang bật hoặc cần tính neo/nối nhưng {parameterError}");
            // A spliced layer needs a stagger the planner can lay out. ReversedEndForEnd is the one
            // it builds; the alternative the enum carries would put every splice on one section.
            if (rule.LapSplice?.Requirement == AbutmentSpliceRequirement.Required &&
                rule.LapSplice.Stagger != AbutmentSpliceStagger.ReversedEndForEnd)
                Error("ABUTMENT_RULE_LAP_STAGGER_UNSUPPORTED",
                    $"{rule.RuleId}: runtime chỉ dựng được sơ đồ nối so le ReversedEndForEnd, " +
                    $"rule khai {rule.LapSplice.Stagger}.");
        }

        // The lap splice of every rule is re-derived, enabled or not: a frozen rule is exactly where a
        // hand-typed lap would sit unnoticed until the day someone unfreezes it.
        foreach (var rule in Rules.Where(rule => rule.LapSplice is not null))
        {
            var splice = rule.LapSplice!;
            if (splice.Requirement == AbutmentSpliceRequirement.Unresolved)
                Error("ABUTMENT_RULE_LAP_REQUIREMENT_UNRESOLVED",
                    $"{rule.RuleId}: lapSplice phải nói rõ thanh có phải nối hay không.");
            if (splice.Requirement != AbutmentSpliceRequirement.Required) continue;

            if (splice.Stagger == AbutmentSpliceStagger.Unresolved)
                Error("ABUTMENT_RULE_LAP_STAGGER_UNRESOLVED",
                    $"{rule.RuleId}: lapSplice phải khai cách so le mối nối theo ghi chú bản vẽ.");
            if (splice.SplicedFraction == AbutmentSplicedFractionBand.Unresolved)
                Error("ABUTMENT_RULE_LAP_SPLICED_FRACTION_UNRESOLVED",
                    $"{rule.RuleId}: lapSplice chưa khai tỷ lệ thanh nối nên không tra được bảng §5.11.5.3.1.");
            if (!double.IsFinite(splice.CuttingModuleMm) || splice.CuttingModuleMm <= 0)
                Error("ABUTMENT_RULE_LAP_CUTTING_MODULE_INVALID",
                    $"{rule.RuleId}: cuttingModuleMm phải dương hữu hạn.");
            if (!double.IsFinite(splice.SpliceCentreStaggerFraction) ||
                splice.SpliceCentreStaggerFraction <= 0 || splice.SpliceCentreStaggerFraction >= 1)
                Error("ABUTMENT_RULE_LAP_STAGGER_FRACTION_INVALID",
                    $"{rule.RuleId}: spliceCentreStaggerFraction phải nằm trong khoảng mở (0; 1).");
            if (string.IsNullOrWhiteSpace(splice.SourceEvidenceId) &&
                string.IsNullOrWhiteSpace(splice.EngineerApprovalReference))
                Error("ABUTMENT_RULE_LAP_SOURCE_MISSING",
                    $"{rule.RuleId}: lapSplice thiếu evidence hoặc phê duyệt kỹ sư.");
            else if (!string.IsNullOrWhiteSpace(splice.SourceEvidenceId) &&
                     !evidenceIds.Contains(splice.SourceEvidenceId))
                Error("ABUTMENT_RULE_LAP_SOURCE_INVALID",
                    $"{rule.RuleId}: evidence lapSplice không tồn tại.");
            // A lapped bar carries the splice in both places it is visible: the continuity kind that
            // says how the bar is made continuous, and the development status that says the ends need
            // an anchorage length instead of stopping at the cover line.
            if (rule.Continuity.Kind != AbutmentContinuityKind.Lap ||
                !rule.Continuity.Stagger)
                Error("ABUTMENT_RULE_LAP_CONTINUITY_MISMATCH",
                    $"{rule.RuleId}: lapSplice Required đòi continuity.kind=Lap và stagger=true, " +
                    $"nhưng rule khai {rule.Continuity.Kind} với stagger={rule.Continuity.Stagger}.");
            if (rule.Development.Status != AbutmentDevelopmentStatus.RequiredLength)
                Error("ABUTMENT_RULE_LAP_DEVELOPMENT_MISMATCH",
                    $"{rule.RuleId}: lapSplice Required đòi development.status=RequiredLength, " +
                    $"nhưng rule khai {rule.Development.Status}.");

            if (!TryResolveDeclaredLapSplice(rule, out var computed, out var spliceError))
            {
                Error("ABUTMENT_RULE_LAP_LENGTH_UNRESOLVED",
                    $"{rule.RuleId}: không tính lại được nối chồng từ tiêu chuẩn — {spliceError}");
                continue;
            }
            if (Math.Abs(computed.RequiredMm - splice.LapLengthMm) >
                AbutmentDevelopmentLengthKernel.LengthToleranceMm)
                Error("ABUTMENT_RULE_LAP_LENGTH_MISMATCH",
                    $"{rule.RuleId}: lapLengthMm khai {splice.LapLengthMm:0.###} mm nhưng " +
                    $"AbutmentDevelopmentLengthKernel cho {computed.RequiredMm:0.###} mm với " +
                    $"D{rule.DiameterMm:0.#}, {computed.SpliceClass} và bước làm tròn " +
                    $"{splice.LapDetailingModuleMm:0.#} mm.");
            if (computed.SpliceClass != splice.SpliceClass)
                Error("ABUTMENT_RULE_LAP_CLASS_MISMATCH",
                    $"{rule.RuleId}: spliceClass khai {splice.SpliceClass} nhưng bảng §5.11.5.3.1 cho " +
                    $"{computed.SpliceClass} với tỷ lệ nối {splice.SplicedFraction}.");
            if (rule.Development.Status == AbutmentDevelopmentStatus.RequiredLength &&
                Math.Abs(computed.Development.RequiredMm - rule.Development.RequiredLengthMm) >
                AbutmentDevelopmentLengthKernel.LengthToleranceMm)
                Error("ABUTMENT_RULE_DEVELOPMENT_LENGTH_MISMATCH",
                    $"{rule.RuleId}: development.requiredLengthMm khai " +
                    $"{rule.Development.RequiredLengthMm:0.###} mm nhưng chiều dài neo kéo §5.11.2.1 " +
                    $"của D{rule.DiameterMm:0.#} là {computed.Development.RequiredMm:0.###} mm.");
        }

        var hash = ComputeHash();
        if (!string.IsNullOrWhiteSpace(RuleHash) && !RuleHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            Error("ABUTMENT_RULE_HASH_MISMATCH", "ruleHash không khớp nội dung preset.");
        return issues;
    }

    private static bool HasAllowedCanonicalPrefix(string mark)
    {
        var normalized = mark.Trim().ToUpperInvariant();
        return normalized.StartsWith("A", StringComparison.Ordinal) ||
               normalized.StartsWith("B", StringComparison.Ordinal) ||
               normalized.StartsWith("C", StringComparison.Ordinal) ||
               normalized.StartsWith("D", StringComparison.Ordinal) ||
               normalized.StartsWith("P", StringComparison.Ordinal) ||
               normalized.StartsWith("N", StringComparison.Ordinal) ||
               normalized.StartsWith("S", StringComparison.Ordinal) ||
               normalized.StartsWith("F", StringComparison.Ordinal);
    }

    private static bool CanonicalPrefixMatchesZone(AbutmentZoneRule rule)
    {
        var prefix = char.ToUpperInvariant(rule.CanonicalMark.Trim()[0]);
        return rule.Zone switch
        {
            AbutmentZoneKind.Footing => prefix is 'A' or 'F',
            AbutmentZoneKind.Stem or AbutmentZoneKind.Backwall or
                AbutmentZoneKind.BearingSeat or AbutmentZoneKind.ShearKey or
                AbutmentZoneKind.Pedestal => prefix is 'B' or 'F',
            AbutmentZoneKind.WingwallLeft or AbutmentZoneKind.WingwallRight => prefix is 'C' or 'F',
            AbutmentZoneKind.PileHead => prefix is 'P' or 'F',
            _ => false
        };
    }

    private static bool IsFootingMatTemplate(AbutmentGeometryTemplate template) =>
        template is AbutmentGeometryTemplate.BottomLongitudinal
            or AbutmentGeometryTemplate.BottomTransverse
            or AbutmentGeometryTemplate.TopLongitudinal
            or AbutmentGeometryTemplate.TopTransverse;

    private static readonly AbutmentResolvedStandardParameters EmptyStandardParameters = new()
    {
        ConcreteStrengthMpa = 0,
        BarGrade = string.Empty,
        YieldStrengthMpa = 0,
        ExposureClass = AbutmentExposureClass.Unresolved,
        WaterCementRatio = 0,
        MaximumAggregateSizeMm = 0,
        StockBarLengthMm = 0,
        SteelDensityKgPerM3 = 0
    };

    private static string FirstNonBlank(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string Describe(double? value) =>
        value is { } present ? present.ToString("0.###") : "chưa khai";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
