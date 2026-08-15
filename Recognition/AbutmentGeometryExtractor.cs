using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

public sealed class AbutmentGeometryExtractor
{
    private readonly Document _document;

    public AbutmentGeometryExtractor(Document document) => _document = document;

    public static bool MatchesProfileIdentity(Element host, AbutmentRebarPresetV1 preset)
    {
        if (host.Category?.Id != new ElementId(BuiltInCategory.OST_GenericModel) ||
            host is not FamilyInstance instance)
            return false;
        var familyName = instance.Symbol?.FamilyName ?? string.Empty;
        var typeName = ResolveTypeName(instance.Symbol);
        // Family/type tokens are the hard gate for resolver open-path.
        // Item parameters (HẠNG MỤC...) are supporting evidence only: empty values must not
        // prevent opening UI when the family name already identifies an abutment host.
        return AbutmentKernel.IdentityContainsAny(familyName, preset.Topology.FamilyNameContains) &&
               AbutmentKernel.IdentityContainsAny(typeName, preset.Topology.TypeNameContains);
    }

    public AbutmentClassification Extract(Element host, AbutmentRebarPresetV1 preset)
    {
        var result = new AbutmentClassification { Status = AbutmentResolutionStatus.Unsupported };
        if (host.Category?.Id != new ElementId(BuiltInCategory.OST_GenericModel))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_HOST_CATEGORY_UNSUPPORTED", ValidationSeverity.Error,
                "Host must be a Generic Model.", host.Id));
            return result;
        }
        if (!RebarHostData.IsValidHost(host))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_REBAR_HOST_DISABLED", ValidationSeverity.Error,
                "Generic Model family is not enabled as a valid rebar host.", host.Id));
            return result;
        }
        if (host is not FamilyInstance instance)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_FAMILY_INSTANCE_REQUIRED", ValidationSeverity.Error,
                "Host must be a FamilyInstance.", host.Id));
            return result;
        }

        var familyName = instance.Symbol?.FamilyName ?? string.Empty;
        var typeName = ResolveTypeName(instance.Symbol);
        if (!AbutmentKernel.IdentityContainsAny(familyName, preset.Topology.FamilyNameContains))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_FAMILY_IDENTITY_UNSUPPORTED", ValidationSeverity.Error,
                $"Family does not match preset: '{familyName}' (type '{typeName}', " +
                $"typeId {(instance.Symbol is null ? -1 : ElementIdCompat.ToLong(instance.Symbol.Id))}).", host.Id));
            return result;
        }
        if (!AbutmentKernel.IdentityContainsAny(typeName, preset.Topology.TypeNameContains))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_TYPE_IDENTITY_UNSUPPORTED", ValidationSeverity.Error,
                $"Type does not match the explicit preset constraint: '{typeName}' " +
                $"(family '{familyName}', typeId {(instance.Symbol is null ? -1 : ElementIdCompat.ToLong(instance.Symbol.Id))}).", host.Id));
            return result;
        }
        if (!HasItemIdentity(host, preset.Topology))
        {
            // Soft gate: family name already matched. Missing/blank HẠNG MỤC is common on pilot models.
            result.Issues.Add(new ValidationIssue("ABUTMENT_ITEM_PARAMETER_UNRESOLVED", ValidationSeverity.Warning,
                "Chưa đọc được HẠNG MỤC/ITEM nhận diện mố trên instance. Tiếp tục bằng family name + geometry/PROFILE_TM.",
                host.Id));
        }
        if (!HasConcreteEvidence(host))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_CONCRETE_MATERIAL_UNRESOLVED", ValidationSeverity.Error,
                "No concrete/precast material evidence was found on host geometry or parameters.", host.Id));
            return result;
        }

        var componentHosts = ResolveComponentHosts(host, preset.Topology.HostAssemblyStrategy);
        if (componentHosts.Count == 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_COMPONENT_ASSEMBLY_UNRESOLVED",
                ValidationSeverity.Error,
                "Profile yêu cầu Revit assembly nhưng host không thuộc assembly có component bê tông/Rebar host hợp lệ.",
                host.Id));
            return result;
        }
        var componentSolids = new List<(ElementId HostId, Solid Solid)>();
        var options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        var hiddenSolidVolumes = new List<double>();
        foreach (var componentHost in componentHosts)
        {
            var solids = new List<Solid>();
            CollectSolids(
                componentHost.get_Geometry(options), Transform.Identity, solids, hiddenSolidVolumes);
            componentSolids.AddRange(solids.Select(solid => (componentHost.Id, solid)));
        }
        var hiddenGeometry = AbutmentHiddenGeometryKernel.Describe(
            componentSolids.Select(item => item.Solid.Volume).ToList(), hiddenSolidVolumes);
        if (hiddenGeometry.HasExclusions)
        {
            result.Issues.Add(new ValidationIssue(
                AbutmentHiddenGeometryKernel.ExcludedCode,
                ValidationSeverity.Warning,
                hiddenGeometry.Message,
                host.Id));
        }
        if (componentSolids.Count == 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_SOLID_MISSING", ValidationSeverity.Error,
                "Assembly host contains no positive-volume solid.", host.Id));
            return result;
        }
        var worldSolids = componentSolids.Select(item => item.Solid).ToList();
        var dimensions = ReadFamilyDimensions(instance, preset.Topology);
        var footingLength = PreferMm(dimensions.FootingLengthMm, preset.Topology.FootingLengthMm);
        var footingWidth = PreferMm(dimensions.FootingWidthMm, preset.Topology.FootingWidthMm);
        if (!TryBuildFrame(instance, worldSolids, footingLength, footingWidth, out var frame, out var frameError,
                out var frameNote))
        {
            result.Status = AbutmentResolutionStatus.Ambiguous;
            result.Issues.Add(new ValidationIssue("ABUTMENT_LOCAL_FRAME_AMBIGUOUS", ValidationSeverity.Error,
                frameError, host.Id));
            return result;
        }
        if (!string.IsNullOrEmpty(frameNote))
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_LOCAL_FRAME_AXIS_FROM_FAMILY_CONVENTION",
                ValidationSeverity.Warning, frameNote, host.Id));
        }

        var descriptors = new List<AbutmentSolidDescriptor>();
        var worldVertices = new List<XYZ>();
        foreach (var component in componentSolids)
        {
            var points = Vertices(component.Solid).ToList();
            if (points.Count == 0) continue;
            var local = points.Select(frame.ToLocal).ToList();
            descriptors.Add(new AbutmentSolidDescriptor
            {
                HostId = component.HostId,
                Solid = component.Solid,
                Volume = component.Solid.Volume,
                LocalBox = Box(local)
            });
            worldVertices.AddRange(points);
        }
        if (descriptors.Count == 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_SOLID_VERTICES_MISSING", ValidationSeverity.Error,
                "Host solids contain no usable vertices.", host.Id));
            return result;
        }

        var planarFaces = DescribePlanarFaces(descriptors, frame);
        var overall = Box(descriptors.SelectMany(item => new[] { item.LocalBox.Min, item.LocalBox.Max }));
        var profileMasses = CollectProfileSections(
            instance, frame, preset.Topology, options, out var profileIssues);
        result.Issues.AddRange(profileIssues.Select(issue => new ValidationIssue(
            issue.Code,
            issue.IsError && preset.Topology.RequireProfileMassMarkers
                ? ValidationSeverity.Error
                : ValidationSeverity.Warning,
            issue.Message,
            host.Id)));
        var profileMassHash = AbutmentProfileMassKernel.ComputeEvidenceHash(
            profileMasses.Select(ToProfileMassHashRow));
        AbutmentCoreProfileEvidence? coreProfile = null;
        if (profileMasses.Count > 0)
        {
            if (!TryResolveCoreProfile(
                    profileMasses, preset.Topology, profileMassHash,
                    out coreProfile, out var coreProfileError))
            {
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_CORE_PROFILE_TOPOLOGY_UNRESOLVED",
                    ValidationSeverity.Warning,
                    "Không phân được PROFILE_TM thành Footing/Stem/Backwall theo adapter ProfileTm14V1: " +
                    coreProfileError + " Footing đã resolve độc lập vẫn được giữ; Stem/Backwall bị khóa.",
                    host.Id));
            }
            else
            {
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_CORE_PROFILE_TOPOLOGY_RESOLVED",
                    ValidationSeverity.Info,
                    $"Đã phân {coreProfile.Sections.Count} PROFILE_TM station thành Footing/Stem/Backwall; " +
                    $"center mismatch={UnitUtils.ConvertFromInternalUnits(coreProfile.MaximumCenterInterpolationMismatch, UnitTypeId.Millimeters):0.###} mm.",
                    host.Id));
            }
        }
        var wingProfiles = CollectWingProfileSections(
            instance, frame, preset.Topology, options, profileMasses, out var wingProfileIssues);
        result.Issues.AddRange(wingProfileIssues.Select(issue => new ValidationIssue(
            issue.Code,
            issue.IsError && preset.Topology.RequireWingProfileMarkers
                ? ValidationSeverity.Error
                : ValidationSeverity.Warning,
            issue.Message,
            host.Id)));
        var wingProfileHash = ComputeWingProfileHash(wingProfiles);
        if (wingProfiles.Count > 0)
        {
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_WING_PROFILE_TOPOLOGY_RESOLVED",
                ValidationSeverity.Info,
                $"Đã resolve {wingProfiles.Count} ordered nested wing profiles LEFT/RIGHT + Inner/Outer; " +
                "profile loop là geometry authority, solid chỉ validation.",
                host.Id));
        }

        var solidFootingBoundary = AbutmentFootingBoundaryResolver.Resolve(
            worldVertices, frame, preset.Topology.FootingBoundaryStrategy);
        if (solidFootingBoundary is null)
        {
            result.Status = AbutmentResolutionStatus.Ambiguous;
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_FOOTING_BOUNDARY_UNRESOLVED",
                ValidationSeverity.Error,
                $"Không resolve được soffit boundary thật theo strategy " +
                $"'{preset.Topology.FootingBoundaryStrategy}'. Không dùng convex/AABB giả để tạo thép.",
                host.Id));
            result.Context = new AbutmentHostContext
            {
                Host = host,
                ComponentHosts = componentHosts,
                Frame = frame,
                OverallBox = overall,
                Solids = descriptors,
                PlanarFaces = planarFaces,
                FamilyName = familyName,
                TypeName = typeName,
                GeometryHash = HashGeometry(descriptors, planarFaces, null, [], profileMassHash, wingProfileHash),
                Dimensions = dimensions,
                FootingFootprint = null,
                FootingBoundary = null,
                Piles = [],
                ProfileMasses = profileMasses,
                ProfileMassHash = profileMassHash,
                CoreProfile = coreProfile,
                WingProfiles = wingProfiles,
                WingProfileHash = wingProfileHash
            };
            return result;
        }

        var expectedFootingDepth = UnitUtils.ConvertToInternalUnits(
            preset.Topology.FootingDepthMm, UnitTypeId.Millimeters);
        var footingDepthTolerance = UnitUtils.ConvertToInternalUnits(
            preset.Topology.FootingDepthToleranceMm, UnitTypeId.Millimeters);
        var solidTopResolved = TryResolveFootingTop(
                componentSolids.Select(item => item.Solid).ToList(),
                frame,
                solidFootingBoundary,
                expectedFootingDepth,
                out var footingTopResolution,
                out var footingTopScan,
                out var footingTopError);
        if (solidTopResolved)
        {
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_FOOTING_TOP_RESOLVED",
                ValidationSeverity.Info,
                AbutmentFootingBandKernel.DescribeSelection(footingTopResolution, footingTopScan),
                host.Id));
            if (footingTopResolution.Margin == AbutmentFootingBandKernel.FootingTopMargin.Narrow)
            {
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_FOOTING_TOP_NARROW_MARGIN",
                    ValidationSeverity.Warning,
                    AbutmentFootingBandKernel.DescribeNarrowMargin(footingTopResolution),
                    host.Id));
            }
            if (footingTopResolution.HasLowerCandidate)
            {
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_FOOTING_TOP_LOWER_BAND_PRESENT",
                    ValidationSeverity.Warning,
                    AbutmentFootingBandKernel.DescribeLowerCandidate(footingTopResolution),
                    host.Id));
            }
            // Declared depth is compared afterwards: a model that departs from the drawing is
            // reported without blocking a measured geometry.
            var depthMatchesDeclaration = AbutmentFootingBandKernel.MatchesDeclaredDepth(
                footingTopResolution.MeasuredDepth, expectedFootingDepth, footingDepthTolerance);
            result.Issues.Add(new ValidationIssue(
                depthMatchesDeclaration
                    ? "ABUTMENT_FOOTING_DEPTH_CONFIRMED"
                    : "ABUTMENT_FOOTING_DEPTH_MISMATCH",
                depthMatchesDeclaration ? ValidationSeverity.Info : ValidationSeverity.Warning,
                AbutmentFootingBandKernel.DescribeDeclaredDepth(
                    footingTopResolution, expectedFootingDepth, footingDepthTolerance),
                host.Id));
        }

        var footingBoundary = solidFootingBoundary;
        AbutmentProfileFootingEvidence? profileFooting = null;
        if (profileMasses.Count > 0)
        {
            if (!TryResolveProfileFooting(
                    profileMasses, frame, preset.Topology, solidFootingBoundary,
                    solidTopResolved ? footingTopResolution.TopElevation : null,
                    solidTopResolved ? footingTopResolution.SoffitElevation : null,
                    out var resolvedBoundary, out profileFooting, out var profileGate, out var profileError))
            {
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_PROFILE_FOOTING_INVALID",
                    preset.Topology.RequireProfileMassMarkers
                        ? ValidationSeverity.Error
                        : ValidationSeverity.Warning,
                    profileError,
                    host.Id));
            }
            else
            {
                footingBoundary = resolvedBoundary;
                result.Issues.Add(new ValidationIssue(
                    profileGate == AbutmentProfileSolidGateKernel.Verdict.DepthMismatch
                        ? "ABUTMENT_PROFILE_FOOTING_DEPTH_MISMATCH"
                        : "ABUTMENT_PROFILE_FOOTING_RESOLVED",
                    profileGate == AbutmentProfileSolidGateKernel.Verdict.DepthMismatch
                        ? ValidationSeverity.Warning
                        : ValidationSeverity.Info,
                    profileError,
                    host.Id));
            }
        }
        else if (preset.Topology.RequireProfileMassMarkers)
        {
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_PROFILE_MASS_MISSING",
                ValidationSeverity.Error,
                "Profile này yêu cầu ba nested PROFILE_TM station sections nhưng không đọc được loop hình học.",
                host.Id));
        }

        if (!solidTopResolved && profileFooting is null)
        {
            result.Status = AbutmentResolutionStatus.Ambiguous;
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_FOOTING_TOP_UNRESOLVED",
                ValidationSeverity.Error,
                footingTopError,
                host.Id));
            result.Context = new AbutmentHostContext
            {
                Host = host,
                ComponentHosts = componentHosts,
                Frame = frame,
                OverallBox = overall,
                Solids = descriptors,
                PlanarFaces = planarFaces,
                FamilyName = familyName,
                TypeName = typeName,
                GeometryHash = HashGeometry(descriptors, planarFaces, solidFootingBoundary, [], profileMassHash, wingProfileHash),
                Dimensions = dimensions,
                FootingFootprint = null,
                FootingBoundary = solidFootingBoundary,
                SolidFootingBoundary = solidFootingBoundary,
                Piles = [],
                ProfileMasses = profileMasses,
                ProfileMassHash = profileMassHash,
                CoreProfile = coreProfile,
                WingProfiles = wingProfiles,
                WingProfileHash = wingProfileHash
            };
            return result;
        }

        if (!solidTopResolved)
        {
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_FOOTING_TOP_SOLID_UNRESOLVED",
                ValidationSeverity.Warning,
                "Không chốt được band solid; dùng cao độ top từ PROFILE (PROFILE thắng). " +
                footingTopError,
                host.Id));
        }

        var boundaryLocal = footingBoundary.Vertices.Select(frame.ToLocal).ToList();
        var placement = AbutmentProfileSolidGateKernel.ResolvePlacementElevations(
            profileFooting?.BottomLocalZ ?? boundaryLocal.Min(point => point.Z),
            profileFooting?.TopLocalZ
                ?? (solidTopResolved ? footingTopResolution.TopElevation : boundaryLocal.Max(point => point.Z)),
            solidTopResolved ? footingTopResolution.SoffitElevation : null,
            solidTopResolved ? footingTopResolution.TopElevation : null);
        var footingFootprint = new AbutmentLocalBox
        {
            Min = new XYZ(
                boundaryLocal.Min(point => point.X),
                boundaryLocal.Min(point => point.Y),
                placement.Bottom),
            Max = new XYZ(
                boundaryLocal.Max(point => point.X),
                boundaryLocal.Max(point => point.Y),
                placement.Top)
        };
        var detectedPiles = new AbutmentPileDetector(_document).Detect(componentHosts, preset);
        var boundary2 = footingBoundary.Vertices
            .Select(point => new PlanPoint2(point.X, point.Y))
            .ToList();
        var maximumBarRadius = UnitUtils.ConvertToInternalUnits(
            preset.Rules.Where(rule => rule.Enabled).Select(rule => rule.DiameterMm)
                .DefaultIfEmpty(0).Max() / 2,
            UnitTypeId.Millimeters);
        var pilePlacementMargin = UnitUtils.ConvertToInternalUnits(
            preset.Topology.PilePlacementMarginMm, UnitTypeId.Millimeters);
        var piles = detectedPiles.Where(pile => GeometryKernel.CircleIntersectsPolygon(
            boundary2,
            new PlanPoint2(pile.Center.X, pile.Center.Y),
            pile.PileRadius + pile.Clearance + pilePlacementMargin + maximumBarRadius)).ToList();
        result.Context = new AbutmentHostContext
        {
            Host = host,
            ComponentHosts = componentHosts,
            Frame = frame,
            OverallBox = overall,
            Solids = descriptors,
            PlanarFaces = planarFaces,
            FamilyName = familyName,
            TypeName = typeName,
            GeometryHash = HashGeometry(descriptors, planarFaces, footingBoundary, piles, profileMassHash, wingProfileHash),
            Dimensions = dimensions,
            FootingFootprint = footingFootprint,
            FootingBoundary = footingBoundary,
            SolidFootingBoundary = solidFootingBoundary,
            ProfileFooting = profileFooting,
            Piles = piles,
            ProfileMasses = profileMasses,
            ProfileMassHash = profileMassHash,
            CoreProfile = coreProfile,
            WingProfiles = wingProfiles,
            WingProfileHash = wingProfileHash
        };

        if (profileMasses.Count > 0)
        {
            var names = string.Join(", ",
                profileMasses.Select(mass => mass.TypeName).Distinct(StringComparer.OrdinalIgnoreCase));
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_PROFILE_MASS_RESOLVED",
                ValidationSeverity.Info,
                $"Đã nhận diện {profileMasses.Count} profile station section(s): {names}.",
                host.Id));
        }

        if (preset.Topology.FootingBoundaryStrategy == AbutmentFootingBoundaryStrategy.Parallelogram)
        {
            // The boundary itself is measured and is what every rebar line is built from. The
            // numbers below only answer "is this the abutment the profile describes", so a profile
            // written for another size warns while a family contradicting its own solid blocks.
            var longEdgeMm = UnitUtils.ConvertFromInternalUnits(
                footingBoundary.LongEdgeLength, UnitTypeId.Millimeters);
            var endEdgeMm = UnitUtils.ConvertFromInternalUnits(
                footingBoundary.EndEdgeLength, UnitTypeId.Millimeters);
            var skewDeclared = dimensions.SkewAngleDeclared;
            var expectedLongEdgeMm = skewDeclared && Math.Abs(dimensions.SkewAngleDeg) > 1e-9
                ? footingLength / Math.Cos(Math.Abs(dimensions.SkewAngleDeg) * Math.PI / 180)
                : footingLength;
            var lengthCheck = AbutmentDeclaredDimensionKernel.Compare(
                "Cạnh dọc bệ (mép xiên)", longEdgeMm, expectedLongEdgeMm,
                AbutmentDeclaredDimensionKernel.ResolveSource(
                    dimensions.FootingLengthMm, preset.Topology.FootingLengthMm));
            var widthCheck = AbutmentDeclaredDimensionKernel.Compare(
                "Cạnh ngang bệ", endEdgeMm, footingWidth,
                AbutmentDeclaredDimensionKernel.ResolveSource(
                    dimensions.FootingWidthMm, preset.Topology.FootingWidthMm));
            var angleDelta = Math.Abs(
                footingBoundary.AcuteAngleDeg - (90 - Math.Abs(dimensions.SkewAngleDeg)));
            var angleBlocks = skewDeclared &&
                              angleDelta > AbutmentDeclaredDimensionKernel.SkewToleranceDeg;
            var blocks = angleBlocks || lengthCheck.Blocks || widthCheck.Blocks;
            var warns = !lengthCheck.Matches || !widthCheck.Matches;
            result.Issues.Add(new ValidationIssue(
                "ABUTMENT_SKEW_FOOTING_BOUNDARY",
                blocks ? ValidationSeverity.Error
                    : warns ? ValidationSeverity.Warning
                    : ValidationSeverity.Info,
                $"Biên bệ đo được: góc nhọn {footingBoundary.AcuteAngleDeg:0.###}°" +
                (skewDeclared
                    ? $", family khai góc xéo {dimensions.SkewAngleDeg:0.###}° " +
                      $"(lệch {angleDelta:0.###}° so với dung sai " +
                      $"{AbutmentDeclaredDimensionKernel.SkewToleranceDeg:0.#}°)."
                    : "; không family nào khai góc xéo nên góc đo được là nguồn duy nhất, không đối chiếu.") +
                " " + AbutmentDeclaredDimensionKernel.Describe(lengthCheck) +
                " " + AbutmentDeclaredDimensionKernel.Describe(widthCheck),
                host.Id));
        }
        else
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_CONVEX_BOUNDARY_SOLID_CLIPPED",
                ValidationSeverity.Info,
                $"Đã resolve convex hull {footingBoundary.Vertices.Count} đỉnh; từng thanh sẽ được clip lại " +
                "theo solid thật và offset tại void/recess.", host.Id));
        }
        if (detectedPiles.Count != piles.Count)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_PILES_OUTSIDE_FOOTING_IGNORED",
                ValidationSeverity.Warning,
                $"Ignored {detectedPiles.Count - piles.Count} pile candidate(s) whose centers are outside the footing polygon.",
                host.Id));
        }
        var diameterFallbacks = piles.Where(pile => pile.MeasuredDiameterMm is null).ToList();
        if (diameterFallbacks.Count > 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_PILE_DIAMETER_FALLBACK",
                ValidationSeverity.Warning,
                $"{diameterFallbacks.Count} pile(s) expose no reliable physical diameter; " +
                $"using conservative fallback Ø{preset.Topology.PileDefaultDiameterMm:0} mm.",
                diameterFallbacks.Select(pile => pile.SourceId).ToArray()));
        }
        // The exclusion zone around every pile is sized from whichever diameter won, so a source
        // that disagrees with the approved profile decides how much steel gets cut. Silence here is
        // what let a rotated bounding box pass as a pile a third wider than it really is.
        var diameterDisagreements = piles.Where(pile => pile.DiameterDisagreesWithPreset).ToList();
        if (diameterDisagreements.Count > 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_PILE_DIAMETER_DISAGREES_WITH_PRESET",
                ValidationSeverity.Error,
                $"{diameterDisagreements.Count}/{piles.Count} cọc có đường kính đo được lệch quá dung " +
                $"sai so với preset Ø{preset.Topology.PileDefaultDiameterMm:0} mm. " +
                string.Join(" ", diameterDisagreements.Select(pile =>
                    $"[{ElementIdCompat.ToLong(pile.SourceId)}] {pile.DiameterNote}")),
                diameterDisagreements.Select(pile => pile.SourceId).ToArray()));
        }
        var verticalFallbacks = piles.Where(pile =>
            pile.VerticalEnvelopeEvidence == "PresetPileHeadDepth").ToList();
        if (verticalFallbacks.Count > 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_PILE_VERTICAL_ENVELOPE_FALLBACK",
                ValidationSeverity.Warning,
                $"{verticalFallbacks.Count} pile(s) expose no bounding box; using approved " +
                $"{preset.Topology.PileHeadDepthMm:0} mm pile-head depth.",
                verticalFallbacks.Select(pile => pile.SourceId).ToArray()));
        }
        if (preset.Topology.TypeNameContains.Count == 0)
        {
            result.Issues.Add(new ValidationIssue("ABUTMENT_TYPE_COMPATIBILITY_GEOMETRY_GATED",
                ValidationSeverity.Warning,
                $"Type '{typeName}' is accepted without a name whitelist; compatibility is determined " +
                "by family/item identity and the geometry, footing-boundary, and pile checks in this analysis.",
                host.Id));
        }
        result.Status = result.Issues.Any(issue => issue.Severity == ValidationSeverity.Error)
            ? AbutmentResolutionStatus.Ambiguous
            : AbutmentResolutionStatus.Resolved;
        return result;
    }

    private static string ResolveTypeName(FamilySymbol? symbol)
    {
        if (symbol is null) return string.Empty;
        var parameterName = symbol.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
        if (!string.IsNullOrWhiteSpace(parameterName)) return parameterName!;
        return symbol.Name ?? string.Empty;
    }

    private bool HasConcreteEvidence(Element element)
    {
        foreach (var materialId in element.GetMaterialIds(false))
        {
            if (_document.GetElement(materialId) is not Material material) continue;
            var evidence = $"{material.Name} {material.MaterialClass}";
            if (ContainsConcrete(evidence)) return true;
        }
        return element.Parameters.Cast<Parameter>().Any(parameter =>
            parameter.StorageType == StorageType.String && ContainsConcrete(parameter.AsString() ?? string.Empty));
    }

    private static bool ContainsConcrete(string value)
    {
        var normalized = RemoveDiacritics(value).ToUpperInvariant().Replace(" ", string.Empty);
        return normalized.Contains("CONCRETE") || normalized.Contains("PRECAST") ||
               normalized.Contains("BETONG");
    }

    private static bool HasItemIdentity(Element element, AbutmentTopologyProfile topology)
    {
        if (topology.ItemParameterNames.Count == 0 || topology.ItemValueContains.Count == 0) return true;
        foreach (var name in topology.ItemParameterNames)
        {
            var parameter = element.LookupParameter(name);
            if (parameter is null) continue;
            var value = parameter.AsString() ?? parameter.AsValueString() ?? string.Empty;
            var normalized = RemoveDiacritics(value);
            if (topology.ItemValueContains.Any(token =>
                    normalized.IndexOf(RemoveDiacritics(token), StringComparison.OrdinalIgnoreCase) >= 0)) return true;
        }
        return false;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character == '\u0111' ? 'd' : character == '\u0110' ? 'D' : character);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <param name="excludedVolumes">
    /// Collects the volume of every solid dropped for being switched off. The traversal runs with
    /// <c>IncludeNonVisibleObjects</c> on — that is the only way to reach the nested PROFILE_TM
    /// model lines — so without this filter a family part the author hid still shapes the hull, the
    /// local frame and the layer elevations. Pass null when the caller does not report.
    /// </param>
    private static void CollectSolids(
        GeometryElement? geometry,
        Transform transform,
        ICollection<Solid> target,
        ICollection<double>? excludedVolumes = null)
    {
        if (geometry is null) return;
        foreach (var item in geometry)
        {
            if (item.Visibility != Visibility.Visible)
            {
                if (item is Solid hidden && hidden.Volume > 1e-9) excludedVolumes?.Add(hidden.Volume);
                continue;
            }
            switch (item)
            {
                case Solid solid when solid.Volume > 1e-9 && solid.Faces.Size > 0:
                    try
                    {
                        target.Add(transform.IsIdentity
                            ? solid
                            : SolidUtils.CreateTransformed(solid, transform));
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentOutOfRangeException)
                    {
                    }
                    break;
                case GeometryInstance instance:
                    // One deterministic traversal: symbol geometry in its local coordinates,
                    // composed with the instance transform exactly once.
                    CollectSolids(
                        instance.GetSymbolGeometry(),
                        transform.Multiply(instance.Transform),
                        target,
                        excludedVolumes);
                    break;
            }
        }
    }

    private readonly record struct ProfileCollectionIssue(string Code, string Message, bool IsError);

    private enum NestedProfilePlaneContract
    {
        LongitudinalStation,
        VerticalWingSurface
    }

    private List<AbutmentProfileMassEvidence> CollectProfileSections(
        FamilyInstance hostInstance,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        out List<ProfileCollectionIssue> issues)
    {
        issues = [];
        var familyTokens = topology.ProfileMassFamilyNameContains;
        var typeTokens = topology.ProfileMassTypeNameContains;
        if (familyTokens.Count == 0 && typeTokens.Count == 0) return [];

        var candidates = new Dictionary<long, FamilyInstance>();
        foreach (var id in hostInstance.GetSubComponentIds())
            if (_document.GetElement(id) is FamilyInstance nested)
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        foreach (var nested in new FilteredElementCollector(_document)
                     .OfClass(typeof(FamilyInstance))
                     .Cast<FamilyInstance>())
        {
            if (nested.SuperComponent is not null && nested.SuperComponent.Id == hostInstance.Id)
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        }
        if (_document.IsFamilyDocument)
        {
            foreach (var nested in new FilteredElementCollector(_document)
                         .OfCategory(BuiltInCategory.OST_Mass)
                         .OfClass(typeof(FamilyInstance))
                         .Cast<FamilyInstance>())
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        }

        var found = new List<AbutmentProfileMassEvidence>();
        foreach (var pair in candidates.OrderBy(pair => pair.Key))
        {
            var nested = pair.Value;
            var familyName = nested.Symbol?.FamilyName ?? string.Empty;
            var typeName = ResolveTypeName(nested.Symbol);
            if (!IsProfileMassIdentity(familyName, typeName, familyTokens, typeTokens)) continue;
            if (TryBuildProfileSection(nested, frame, topology, options, familyName, typeName, null,
                    out var evidence, out var error))
            {
                found.Add(evidence);
            }
            else
            {
                issues.Add(new ProfileCollectionIssue(
                    "ABUTMENT_PROFILE_LOOP_INVALID",
                    $"{familyName}:{typeName} ({pair.Key}) không cung cấp closed ModelLine loop hợp lệ: {error}",
                    true));
            }
        }

        // PROFILE_TM is commonly nested as non-shared Mass geometry. In that case Revit
        // exposes no project FamilyInstance, but GeometryInstance still carries a public
        // SymbolGeometryId and occurrence transform. Traverse the selected host only;
        // never fall back to the document-wide symbol catalog.
        if (found.Count == 0)
        {
            var nextSyntheticId = -1L;
            CollectNonSharedProfileSections(
                hostInstance.get_Geometry(options),
                Transform.Identity,
                frame,
                topology,
                familyTokens,
                typeTokens,
                found,
                issues,
                ref nextSyntheticId);
        }

        // Non-shared nested ModelLine families are not guaranteed to survive project-level
        // geometry expansion. Read their owned instances from an independent copy of the
        // selected host family document, then compose host-instance and nested transforms.
        // This remains profile geometry authority; the project solid is never substituted.
        if (found.Count == 0 && !_document.IsFamilyDocument)
            CollectProfileSectionsFromHostFamilyDocument(
                hostInstance, frame, topology, options, familyTokens, typeTokens, found, issues);

        if (found.Count == 0) return [];
        var stationTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfileStationSeparationToleranceMm, UnitTypeId.Millimeters);
        if (!AbutmentProfileMassKernel.TryAssignStationRoles(
                found.Select(item => new AbutmentProfileMassKernel.ProfileStation(
                        item.SourceElementIdValue, item.StationCoordinate))
                    .ToList(),
                stationTolerance,
                out var roles,
                out var roleError))
        {
            issues.Add(new ProfileCollectionIssue(
                "ABUTMENT_PROFILE_ROLES_INVALID",
                $"Không xác lập duy nhất L/Center/R từ vị trí instance: {roleError}",
                true));
            return found;
        }

        return found.Select(item => CopyWithRole(item, roles[item.SourceElementIdValue]))
            .OrderBy(item => item.StationCoordinate)
            .ToList();
    }

    private List<AbutmentWingProfileEvidence> CollectWingProfileSections(
        FamilyInstance hostInstance,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        IReadOnlyList<AbutmentProfileMassEvidence> coreProfiles,
        out List<ProfileCollectionIssue> issues)
    {
        issues = [];
        var familyTokens = topology.WingProfileFamilyNameContains;
        var leftTokens = topology.WingProfileLeftTypeNameContains;
        var rightTokens = topology.WingProfileRightTypeNameContains;
        var typeTokens = leftTokens.Concat(rightTokens)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (familyTokens.Count == 0 && typeTokens.Length == 0) return [];

        var candidates = new Dictionary<long, FamilyInstance>();
        foreach (var id in hostInstance.GetSubComponentIds())
            if (_document.GetElement(id) is FamilyInstance nested)
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        foreach (var nested in new FilteredElementCollector(_document)
                     .OfClass(typeof(FamilyInstance))
                     .Cast<FamilyInstance>())
        {
            if (nested.SuperComponent is not null && nested.SuperComponent.Id == hostInstance.Id)
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        }
        if (_document.IsFamilyDocument)
        {
            foreach (var nested in new FilteredElementCollector(_document)
                         .OfCategory(BuiltInCategory.OST_Mass)
                         .OfClass(typeof(FamilyInstance))
                         .Cast<FamilyInstance>())
                candidates[ElementIdCompat.ToLong(nested.Id)] = nested;
        }

        var found = new List<AbutmentProfileMassEvidence>();
        foreach (var pair in candidates.OrderBy(pair => pair.Key))
        {
            var nested = pair.Value;
            var familyName = nested.Symbol?.FamilyName ?? string.Empty;
            var typeName = ResolveTypeName(nested.Symbol);
            if (!IsProfileMassIdentity(familyName, typeName, familyTokens, typeTokens)) continue;
            if (TryBuildProfileSection(
                    nested, frame, topology, options, familyName, typeName, null,
                    NestedProfilePlaneContract.VerticalWingSurface,
                    out var evidence, out var error))
            {
                found.Add(evidence);
            }
            else
            {
                issues.Add(new ProfileCollectionIssue(
                    "ABUTMENT_WING_PROFILE_LOOP_INVALID",
                    $"{familyName}:{typeName} ({pair.Key}) không cung cấp closed vertical ModelLine loop hợp lệ: {error}",
                    true));
            }
        }

        if (found.Count == 0)
        {
            var nextSyntheticId = -1000000L;
            CollectNonSharedProfileSections(
                hostInstance.get_Geometry(options),
                Transform.Identity,
                frame,
                topology,
                familyTokens,
                typeTokens,
                found,
                issues,
                ref nextSyntheticId,
                NestedProfilePlaneContract.VerticalWingSurface,
                "ABUTMENT_WING_PROFILE",
                "wing profile");
        }

        if (found.Count == 0 && !_document.IsFamilyDocument)
            CollectProfileSectionsFromHostFamilyDocument(
                hostInstance, frame, topology, options, familyTokens, typeTokens,
                found, issues, NestedProfilePlaneContract.VerticalWingSurface,
                "ABUTMENT_WING_PROFILE", "wing profile");

        if (found.Count == 0)
        {
            issues.Add(new ProfileCollectionIssue(
                "ABUTMENT_WING_PROFILE_MISSING",
                "Không đọc được nested wing profile LEFT/RIGHT ModelLine loop từ host đã chọn.",
                true));
            return [];
        }
        if (coreProfiles.Count != 3 || coreProfiles.Any(profile => profile.OrderedLoopLocal.Count < 3))
        {
            issues.Add(new ProfileCollectionIssue(
                "ABUTMENT_WING_PROFILE_CORE_REFERENCE_UNRESOLVED",
                "Wing profile cần đúng ba PROFILE_TM L/Center/R để xác lập core relation mà không dựa vào tên.",
                true));
            return [];
        }

        var coreLongitudinal = coreProfiles.Average(profile => profile.StationCoordinate);
        var coreTransverse = coreProfiles.SelectMany(profile => profile.OrderedLoopLocal)
            .Average(point => point.Y);
        var occurrences = found.Select(profile => new AbutmentWingProfileOccurrenceKernel(
                profile.SourceElementIdValue,
                profile.OccurrenceKey,
                ResolveDeclaredWingSide(profile.TypeName, leftTokens, rightTokens),
                profile.PlaneOriginLocal.X,
                profile.PlaneOriginLocal.Y,
                profile.PlaneNormalLocal.X,
                profile.PlaneNormalLocal.Y))
            .ToArray();
        var stationTolerance = UnitUtils.ConvertToInternalUnits(
            topology.WingProfileStationSeparationToleranceMm, UnitTypeId.Millimeters);
        var directionTolerance = UnitUtils.ConvertToInternalUnits(
            topology.WingProfileDirectionToleranceMm, UnitTypeId.Millimeters);
        if (!AbutmentWingProfileKernel.TryResolveProfileRoles(
                occurrences,
                coreLongitudinal,
                coreTransverse,
                stationTolerance,
                directionTolerance,
                out var roles,
                out var roleError))
        {
            issues.Add(new ProfileCollectionIssue(
                "ABUTMENT_WING_PROFILE_TOPOLOGY_UNRESOLVED",
                roleError,
                true));
            return [];
        }

        var roleByKey = roles.ToDictionary(role => role.OccurrenceKey, StringComparer.Ordinal);
        return found.Select(profile =>
            {
                var role = roleByKey[profile.OccurrenceKey];
                return new AbutmentWingProfileEvidence
                {
                    SourceElementIdValue = profile.SourceElementIdValue,
                    OccurrenceKey = profile.OccurrenceKey,
                    SymbolGeometryIdentifier = profile.SymbolGeometryIdentifier,
                    ParameterChecksum = profile.ParameterChecksum,
                    FamilyName = profile.FamilyName,
                    TypeName = profile.TypeName,
                    DeclaredSide = ResolveDeclaredWingSide(profile.TypeName, leftTokens, rightTokens),
                    Side = role.Side,
                    FaceRole = role.FaceRole,
                    CoreDirectionScore = role.CoreDirectionScore,
                    PlaneOriginLocal = profile.PlaneOriginLocal,
                    PlaneBasisULocal = profile.PlaneBasisULocal,
                    PlaneBasisVLocal = profile.PlaneBasisVLocal,
                    PlaneNormalLocal = profile.PlaneNormalLocal,
                    OrderedLoopLocal = profile.OrderedLoopLocal,
                    MaximumEndpointGap = profile.MaximumEndpointGap,
                    MaximumPlaneDeviation = profile.MaximumPlaneDeviation,
                    GeometryHash = profile.GeometryHash
                };
            })
            .OrderBy(profile => profile.Side)
            .ThenBy(profile => profile.FaceRole)
            .ThenBy(profile => profile.OccurrenceKey, StringComparer.Ordinal)
            .ToList();
    }

    private static AbutmentWingProfileDeclaredSideKernel ResolveDeclaredWingSide(
        string typeName,
        IReadOnlyList<string> leftTokens,
        IReadOnlyList<string> rightTokens)
    {
        var left = AbutmentProfileMassKernel.MatchesAnyToken(typeName, leftTokens);
        var right = AbutmentProfileMassKernel.MatchesAnyToken(typeName, rightTokens);
        if (left == right) return AbutmentWingProfileDeclaredSideKernel.Unknown;
        return left
            ? AbutmentWingProfileDeclaredSideKernel.Left
            : AbutmentWingProfileDeclaredSideKernel.Right;
    }

    private static string ComputeWingProfileHash(IReadOnlyList<AbutmentWingProfileEvidence> profiles)
    {
        if (profiles.Count == 0) return string.Empty;
        static string F(double value) => Math.Round(value, 9)
            .ToString("R", CultureInfo.InvariantCulture);
        var rows = profiles
            .OrderBy(profile => profile.Side)
            .ThenBy(profile => profile.FaceRole)
            .ThenBy(profile => profile.OccurrenceKey, StringComparer.Ordinal)
            .Select(profile => string.Join("|",
                profile.Side,
                profile.FaceRole,
                profile.OccurrenceKey,
                profile.SymbolGeometryIdentifier,
                profile.ParameterChecksum,
                profile.FamilyName.Trim().ToUpperInvariant(),
                profile.TypeName.Trim().ToUpperInvariant(),
                F(profile.CoreDirectionScore),
                F(profile.PlaneOriginLocal.X), F(profile.PlaneOriginLocal.Y), F(profile.PlaneOriginLocal.Z),
                F(profile.PlaneNormalLocal.X), F(profile.PlaneNormalLocal.Y), F(profile.PlaneNormalLocal.Z),
                string.Join(";", profile.OrderedLoopLocal.Select(point =>
                    string.Join(",", F(point.X), F(point.Y), F(point.Z))))));
        using var sha = SHA256.Create();
        return BitConverter.ToString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("||", rows))))
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryBuildProfileSection(
        FamilyInstance nested,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        string familyName,
        string typeName,
        Transform? documentToProjectTransform,
        out AbutmentProfileMassEvidence evidence,
        out string error)
        => TryBuildProfileSection(
            nested, frame, topology, options, familyName, typeName,
            documentToProjectTransform, NestedProfilePlaneContract.LongitudinalStation,
            out evidence, out error);

    private static bool TryBuildProfileSection(
        FamilyInstance nested,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        string familyName,
        string typeName,
        Transform? documentToProjectTransform,
        NestedProfilePlaneContract planeContract,
        out AbutmentProfileMassEvidence evidence,
        out string error)
    {
        var sourceId = ElementIdCompat.ToLong(nested.Id);
        var projectTransform = documentToProjectTransform ?? Transform.Identity;
        var occurrenceTransform = projectTransform.Multiply(nested.GetTransform());
        return TryBuildProfileSectionGeometry(
            nested.get_Geometry(options),
            projectTransform,
            occurrenceTransform,
            sourceId,
            BuildOccurrenceKey(
                nested.Symbol is null
                    ? string.Empty
                    : ElementIdCompat.ToLong(nested.Symbol.Id).ToString(CultureInfo.InvariantCulture),
                occurrenceTransform),
            nested.Symbol is null
                ? string.Empty
                : ElementIdCompat.ToLong(nested.Symbol.Id).ToString(CultureInfo.InvariantCulture),
            ComputeParameterChecksum(nested),
            frame,
            topology,
            familyName,
            typeName,
            planeContract,
            out evidence,
            out error);
    }

    private static bool TryBuildProfileSectionGeometry(
        GeometryElement? geometry,
        Transform geometryTransform,
        Transform occurrenceTransform,
        long sourceId,
        string occurrenceKey,
        string symbolGeometryIdentifier,
        string parameterChecksum,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        string familyName,
        string typeName,
        NestedProfilePlaneContract planeContract,
        out AbutmentProfileMassEvidence evidence,
        out string error)
    {
        evidence = null!;
        error = string.Empty;
        var segments = new List<AbutmentProfileMassKernel.ProfileSegment3>();
        CollectProfileLineSegments(geometry, geometryTransform, frame, segments);
        var closureTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfileClosureToleranceMm, UnitTypeId.Millimeters);
        if (!AbutmentProfileMassKernel.TryOrderClosedLoop(
                segments, closureTolerance, out var ordered, out var endpointGap, out error))
            return false;

        var origin = frame.ToLocal(occurrenceTransform.Origin);
        var basisU = ToLocalVector(frame, occurrenceTransform.BasisX);
        var basisV = ToLocalVector(frame, occurrenceTransform.BasisZ);
        var normal = ToLocalVector(frame, occurrenceTransform.BasisY);
        if (basisU.GetLength() < 0.9 || basisV.GetLength() < 0.9 || normal.GetLength() < 0.9)
        {
            error = "Nested profile transform is degenerate.";
            return false;
        }
        basisU = basisU.Normalize();
        basisV = basisV.Normalize();
        normal = normal.Normalize();
        // The 0.9 here is 25,84°, and the pilot abutment is skewed 25,00°. The threshold is left
        // exactly as it was — tightening it without measuring the real family would risk killing a
        // PROFILE pipeline that currently works — but how close it runs is now recorded.
        var stationAgreement = AbutmentAxisAgreementKernel.Classify(
            normal.X, 0.9, AbutmentAxisAgreementKernel.DefaultWarningBand);
        if (planeContract == NestedProfilePlaneContract.LongitudinalStation &&
            stationAgreement.Agreement == AbutmentAxisAgreementKernel.Agreement.AcceptedNearThreshold)
        {
            AbutmentDiagnostics.LogMessage(
                AbutmentAxisAgreementKernel.NearThresholdCode,
                $"{familyName}:{typeName} ({occurrenceKey}) — " +
                AbutmentAxisAgreementKernel.DescribeNearThreshold(
                    stationAgreement, "Pháp tuyến mặt cắt trạm PROFILE_TM"));
        }

        var planeMatches = planeContract switch
        {
            NestedProfilePlaneContract.LongitudinalStation =>
                Math.Abs(normal.X) >= 0.9 && Math.Abs(basisV.Z) >= 0.9,
            NestedProfilePlaneContract.VerticalWingSurface =>
                Math.Abs(basisV.Z) >= 0.9 && Math.Abs(normal.Z) <= 0.1 && Math.Abs(basisU.Z) <= 0.1,
            _ => false
        };
        if (!planeMatches)
        {
            error = planeContract == NestedProfilePlaneContract.LongitudinalStation
                ? "Nested profile plane is not a longitudinal station section with a vertical axis."
                : "Nested wing profile is not a vertical surface loop with a horizontal plane normal.";
            return false;
        }
        var planeOrigin = new AbutmentProfileMassKernel.ProfilePoint3(origin.X, origin.Y, origin.Z);
        var planeNormal = new AbutmentProfileMassKernel.ProfilePoint3(normal.X, normal.Y, normal.Z);
        var planarity = AbutmentProfileMassKernel.MaximumPlaneDeviation(ordered, planeOrigin, planeNormal);
        var planarityTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfilePlanarityToleranceMm, UnitTypeId.Millimeters);
        if (planarity > planarityTolerance)
        {
            error = $"Profile planarity deviation {planarity:R} exceeds tolerance.";
            return false;
        }
        if (AbutmentProfileMassKernel.HasSelfIntersection(
                ordered,
                planeOrigin,
                new AbutmentProfileMassKernel.ProfilePoint3(basisU.X, basisU.Y, basisU.Z),
                new AbutmentProfileMassKernel.ProfilePoint3(basisV.X, basisV.Y, basisV.Z),
                closureTolerance))
        {
            error = "Profile loop self-intersects.";
            return false;
        }

        var loop = ordered.Select(point => new XYZ(point.X, point.Y, point.Z)).ToList();
        var min = new XYZ(loop.Min(point => point.X), loop.Min(point => point.Y), loop.Min(point => point.Z));
        var max = new XYZ(loop.Max(point => point.X), loop.Max(point => point.Y), loop.Max(point => point.Z));
        evidence = new AbutmentProfileMassEvidence
        {
            SourceElementIdValue = sourceId,
            OccurrenceKey = occurrenceKey,
            SymbolGeometryIdentifier = symbolGeometryIdentifier,
            ParameterChecksum = parameterChecksum,
            FamilyName = familyName,
            TypeName = typeName,
            Role = AbutmentProfileMassKernel.ProfileStationRole.Unknown,
            StationCoordinate = origin.X,
            OriginX = origin.X,
            OriginY = origin.Y,
            OriginZ = origin.Z,
            MinX = min.X,
            MinY = min.Y,
            MinZ = min.Z,
            MaxX = max.X,
            MaxY = max.Y,
            MaxZ = max.Z,
            PlaneOriginLocal = origin,
            PlaneBasisULocal = basisU,
            PlaneBasisVLocal = basisV,
            PlaneNormalLocal = normal,
            OrderedLoopLocal = loop,
            MaximumEndpointGap = endpointGap,
            MaximumPlaneDeviation = planarity,
            GeometryHash = ComputeProfileLoopHash(loop, origin, basisU, basisV, normal)
        };
        return true;
    }

    private static void CollectNonSharedProfileSections(
        GeometryElement? geometry,
        Transform parentTransform,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        IReadOnlyList<string> familyTokens,
        IReadOnlyList<string> typeTokens,
        ICollection<AbutmentProfileMassEvidence> found,
        ICollection<ProfileCollectionIssue> issues,
        ref long nextSyntheticId)
        => CollectNonSharedProfileSections(
            geometry, parentTransform, frame, topology, familyTokens, typeTokens,
            found, issues, ref nextSyntheticId, NestedProfilePlaneContract.LongitudinalStation,
            "ABUTMENT_PROFILE", "PROFILE_TM");

    private static void CollectNonSharedProfileSections(
        GeometryElement? geometry,
        Transform parentTransform,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        IReadOnlyList<string> familyTokens,
        IReadOnlyList<string> typeTokens,
        ICollection<AbutmentProfileMassEvidence> found,
        ICollection<ProfileCollectionIssue> issues,
        ref long nextSyntheticId,
        NestedProfilePlaneContract planeContract,
        string issuePrefix,
        string profileLabel)
    {
        if (geometry is null) return;
        foreach (var item in geometry)
        {
            if (item is not GeometryInstance instance) continue;
            var occurrenceTransform = parentTransform.Multiply(instance.Transform);
            FamilySymbol? symbol = null;
            string symbolIdentifier = string.Empty;
            try
            {
                var symbolGeometryId = instance.GetSymbolGeometryId();
                symbolIdentifier = ElementIdCompat.ToLong(symbolGeometryId.SymbolId)
                    .ToString(CultureInfo.InvariantCulture);
                symbol = instance.GetDocument().GetElement(symbolGeometryId.SymbolId) as FamilySymbol;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
            }

            var familyName = symbol?.FamilyName ?? string.Empty;
            var typeName = ResolveTypeName(symbol);
            var symbolGeometry = instance.GetSymbolGeometry();
            if (symbol is not null &&
                IsProfileMassIdentity(familyName, typeName, familyTokens, typeTokens))
            {
                var sourceId = nextSyntheticId--;
                var occurrenceKey = BuildOccurrenceKey(symbolIdentifier, occurrenceTransform);
                if (TryBuildProfileSectionGeometry(
                        symbolGeometry,
                        occurrenceTransform,
                        occurrenceTransform,
                        sourceId,
                        occurrenceKey,
                        symbolIdentifier,
                        ComputeParameterChecksum(symbol),
                        frame,
                        topology,
                        familyName,
                        typeName,
                        planeContract,
                        out var evidence,
                        out var error))
                {
                    found.Add(evidence);
                }
                else
                {
                    issues.Add(new ProfileCollectionIssue(
                        issuePrefix + "_LOOP_INVALID",
                        $"{familyName}:{typeName} ({occurrenceKey}) không cung cấp closed {profileLabel} ModelLine loop hợp lệ: {error}",
                        true));
                }
                continue;
            }

            CollectNonSharedProfileSections(
                symbolGeometry,
                occurrenceTransform,
                frame,
                topology,
                familyTokens,
                typeTokens,
                found,
                issues,
                ref nextSyntheticId,
                planeContract,
                issuePrefix,
                profileLabel);
        }
    }

    private static string BuildOccurrenceKey(string symbolIdentifier, Transform transform)
    {
        static string F(double value) => Math.Round(value, 9)
            .ToString("R", CultureInfo.InvariantCulture);
        return string.Join("|", "SYMBOL", symbolIdentifier,
            F(transform.Origin.X), F(transform.Origin.Y), F(transform.Origin.Z),
            F(transform.BasisX.X), F(transform.BasisX.Y), F(transform.BasisX.Z),
            F(transform.BasisY.X), F(transform.BasisY.Y), F(transform.BasisY.Z),
            F(transform.BasisZ.X), F(transform.BasisZ.Y), F(transform.BasisZ.Z));
    }

    private void CollectProfileSectionsFromHostFamilyDocument(
        FamilyInstance hostInstance,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        IReadOnlyList<string> familyTokens,
        IReadOnlyList<string> typeTokens,
        ICollection<AbutmentProfileMassEvidence> found,
        ICollection<ProfileCollectionIssue> issues)
        => CollectProfileSectionsFromHostFamilyDocument(
            hostInstance, frame, topology, options, familyTokens, typeTokens,
            found, issues, NestedProfilePlaneContract.LongitudinalStation,
            "ABUTMENT_PROFILE", "PROFILE_TM");

    private void CollectProfileSectionsFromHostFamilyDocument(
        FamilyInstance hostInstance,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        Options options,
        IReadOnlyList<string> familyTokens,
        IReadOnlyList<string> typeTokens,
        ICollection<AbutmentProfileMassEvidence> found,
        ICollection<ProfileCollectionIssue> issues,
        NestedProfilePlaneContract planeContract,
        string issuePrefix,
        string profileLabel)
    {
        var family = hostInstance.Symbol?.Family;
        if (family is null || family.IsInPlace || !family.IsEditable)
        {
            issues.Add(new ProfileCollectionIssue(
                issuePrefix + "_FAMILY_DOCUMENT_UNAVAILABLE",
                $"Host family không cho phép mở family document để đọc nested {profileLabel}.",
                true));
            return;
        }

        Document? familyDocument = null;
        var closeWhenDone = false;
        try
        {
            var alreadyOpen = _document.Application.Documents.Cast<Document>()
                .Where(document => document.IsFamilyDocument &&
                                   string.Equals(
                                       document.OwnerFamily?.Name,
                                       family.Name,
                                       StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (alreadyOpen.Count > 1)
                throw new InvalidOperationException(
                    $"Có {alreadyOpen.Count} family document cùng identity '{family.Name}'.");
            if (alreadyOpen.Count == 1)
            {
                familyDocument = alreadyOpen[0];
            }
            else
            {
                if (_document.IsModifiable || _document.IsReadOnly)
                    throw new InvalidOperationException(
                        "Project document is modifiable/read-only; EditFamily is not safe in this API context.");
                familyDocument = _document.EditFamily(family);
                closeWhenDone = true;
            }

            var hostToProject = hostInstance.GetTransform();
            foreach (var nested in new FilteredElementCollector(familyDocument)
                         .OfClass(typeof(FamilyInstance))
                         .Cast<FamilyInstance>()
                         .OrderBy(instance => ElementIdCompat.ToLong(instance.Id)))
            {
                var familyName = nested.Symbol?.FamilyName ?? string.Empty;
                var typeName = ResolveTypeName(nested.Symbol);
                if (!IsProfileMassIdentity(familyName, typeName, familyTokens, typeTokens)) continue;
                if (TryBuildProfileSection(
                        nested, frame, topology, options, familyName, typeName, hostToProject,
                        planeContract,
                        out var evidence, out var error))
                {
                    found.Add(evidence);
                }
                else
                {
                    issues.Add(new ProfileCollectionIssue(
                        issuePrefix + "_FAMILY_LOOP_INVALID",
                        $"{familyName}:{typeName} trong host family không cung cấp closed ModelLine loop: {error}",
                        true));
                }
            }
        }
        catch (Exception exception)
        {
            issues.Add(new ProfileCollectionIssue(
                issuePrefix + "_FAMILY_DOCUMENT_UNAVAILABLE",
                $"Không đọc được nested {profileLabel} từ family document '{family.Name}': {exception.Message}",
                true));
        }
        finally
        {
            if (closeWhenDone && familyDocument is { IsValidObject: true })
            {
                try
                {
                    familyDocument.Close(false);
                }
                catch (Exception exception)
                {
                    issues.Add(new ProfileCollectionIssue(
                        issuePrefix + "_FAMILY_DOCUMENT_CLOSE_FAILED",
                        $"Không đóng được family document read-only copy: {exception.Message}",
                        true));
                }
            }
        }
    }

    private static string ComputeParameterChecksum(Element element)
    {
        static string Value(Parameter parameter) => parameter.StorageType switch
        {
            StorageType.Double => parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
            StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.String => parameter.AsString() ?? string.Empty,
            StorageType.ElementId => parameter.AsValueString() ?? string.Empty,
            _ => string.Empty
        };
        var owners = element is FamilyInstance instance && instance.Symbol is not null
            ? new Element[] { element, instance.Symbol }
            : new[] { element };
        var rows = owners.SelectMany(owner => owner.Parameters.Cast<Parameter>())
            .Where(parameter => parameter.Definition is not null)
            .Select(parameter => string.Join("|",
                parameter.Definition.Name.Trim().ToUpperInvariant(),
                parameter.StorageType,
                Value(parameter).Trim().ToUpperInvariant()))
            .OrderBy(row => row, StringComparer.Ordinal);
        using var sha = SHA256.Create();
        return BitConverter.ToString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", rows))))
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private static void CollectProfileLineSegments(
        GeometryElement? geometry,
        Transform transform,
        AbutmentLocalFrame frame,
        ICollection<AbutmentProfileMassKernel.ProfileSegment3> target)
    {
        if (geometry is null) return;
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Line line when line.IsBound:
                    var start = frame.ToLocal(transform.OfPoint(line.GetEndPoint(0)));
                    var end = frame.ToLocal(transform.OfPoint(line.GetEndPoint(1)));
                    target.Add(new AbutmentProfileMassKernel.ProfileSegment3(
                        new AbutmentProfileMassKernel.ProfilePoint3(start.X, start.Y, start.Z),
                        new AbutmentProfileMassKernel.ProfilePoint3(end.X, end.Y, end.Z)));
                    break;
                case GeometryInstance instance:
                    CollectProfileLineSegments(
                        instance.GetSymbolGeometry(),
                        transform.Multiply(instance.Transform),
                        frame,
                        target);
                    break;
            }
        }
    }

    private static bool IsProfileMassIdentity(
        string familyName,
        string typeName,
        IReadOnlyList<string> familyTokens,
        IReadOnlyList<string> typeTokens)
    {
        var familyOk = familyTokens.Count == 0 ||
                       AbutmentProfileMassKernel.MatchesAnyToken(familyName, familyTokens);
        var typeOk = typeTokens.Count == 0 ||
                     AbutmentProfileMassKernel.MatchesAnyToken(typeName, typeTokens);
        if (familyTokens.Count > 0 && typeTokens.Count > 0)
            return familyOk && typeOk;
        if (familyTokens.Count > 0) return familyOk;
        return typeOk;
    }

    private static AbutmentProfileMassEvidence CopyWithRole(
        AbutmentProfileMassEvidence source,
        AbutmentProfileMassKernel.ProfileStationRole role) => new()
    {
        SourceElementIdValue = source.SourceElementIdValue,
        OccurrenceKey = source.OccurrenceKey,
        SymbolGeometryIdentifier = source.SymbolGeometryIdentifier,
        ParameterChecksum = source.ParameterChecksum,
        FamilyName = source.FamilyName,
        TypeName = source.TypeName,
        Role = role,
        StationCoordinate = source.StationCoordinate,
        OriginX = source.OriginX,
        OriginY = source.OriginY,
        OriginZ = source.OriginZ,
        MinX = source.MinX,
        MinY = source.MinY,
        MinZ = source.MinZ,
        MaxX = source.MaxX,
        MaxY = source.MaxY,
        MaxZ = source.MaxZ,
        PlaneOriginLocal = source.PlaneOriginLocal,
        PlaneBasisULocal = source.PlaneBasisULocal,
        PlaneBasisVLocal = source.PlaneBasisVLocal,
        PlaneNormalLocal = source.PlaneNormalLocal,
        OrderedLoopLocal = source.OrderedLoopLocal,
        MaximumEndpointGap = source.MaximumEndpointGap,
        MaximumPlaneDeviation = source.MaximumPlaneDeviation,
        GeometryHash = source.GeometryHash
    };

    private static AbutmentProfileMassKernel.ProfileMassHashRow ToProfileMassHashRow(
        AbutmentProfileMassEvidence mass) => new(
        mass.SourceElementIdValue,
        mass.FamilyName,
        mass.TypeName,
        mass.ParameterChecksum,
        mass.Role,
        mass.OriginX,
        mass.OriginY,
        mass.OriginZ,
        mass.MinX,
        mass.MinY,
        mass.MinZ,
        mass.MaxX,
        mass.MaxY,
        mass.MaxZ,
        mass.OrderedLoopLocal.Select(point =>
            new AbutmentProfileMassKernel.ProfilePoint3(point.X, point.Y, point.Z)).ToList());

    private static XYZ ToLocalVector(AbutmentLocalFrame frame, XYZ vector) => new(
        vector.DotProduct(frame.Longitudinal),
        vector.DotProduct(frame.Transverse),
        vector.DotProduct(frame.Vertical));

    private static string ComputeProfileLoopHash(
        IReadOnlyList<XYZ> loop,
        XYZ origin,
        XYZ basisU,
        XYZ basisV,
        XYZ normal)
    {
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var values = new[] { origin, basisU, basisV, normal }.Concat(loop)
            .Select(point => string.Join(",", F(point.X), F(point.Y), F(point.Z)));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", values))))
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryResolveCoreProfile(
        IReadOnlyCollection<AbutmentProfileMassEvidence> profiles,
        AbutmentTopologyProfile topology,
        string profileMassHash,
        out AbutmentCoreProfileEvidence evidence,
        out string error)
    {
        evidence = null!;
        error = string.Empty;
        if (profiles.Count != 3)
        {
            error = $"ProfileTm14V1 requires exactly three L/Center/R sections; found {profiles.Count}.";
            return false;
        }

        var closureTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfileClosureToleranceMm, UnitTypeId.Millimeters);
        var signatureTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfilePlanarityToleranceMm, UnitTypeId.Millimeters);
        var interpolationTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfileToSolidMismatchToleranceMm, UnitTypeId.Millimeters);
        var sections = new List<AbutmentCoreProfileSectionEvidence>();
        foreach (var profile in profiles.OrderBy(item => item.StationCoordinate))
        {
            var origin = profile.PlaneOriginLocal;
            var horizontal = profile.PlaneBasisULocal.Normalize();
            var vertical = profile.PlaneBasisVLocal.Normalize();
            // Canonical section coordinates use positive local transverse and vertical axes.
            // This is a coordinate orientation only; physical points are never mirrored.
            if (horizontal.Y < 0) horizontal = -horizontal;
            if (vertical.Z < 0) vertical = -vertical;
            var projected = profile.OrderedLoopLocal.Select(point =>
                new PlanPoint2(
                    (point - origin).DotProduct(horizontal),
                    (point - origin).DotProduct(vertical))).ToArray();
            var segments = Enumerable.Range(0, projected.Length)
                .Select(index => new PlanSegment2(
                    projected[index], projected[(index + 1) % projected.Length]))
                .ToArray();
            if (!TryPartitionSection(
                    segments, closureTolerance, signatureTolerance, interpolationTolerance,
                    out var partition, out var sectionError))
            {
                error = $"{profile.Role}: {sectionError}";
                return false;
            }
            if (partition.AdapterId != ProfileTm14AdapterId)
            {
                AbutmentDiagnostics.LogMessage(
                    "ABUTMENT_CORE_PROFILE_ADAPTER_FALLBACK",
                    $"{profile.Role}: chữ ký {ProfileTm14AdapterId} không khớp nên đã phân dải bằng " +
                    $"{partition.AdapterId}. Lý do trượt chữ ký: {partition.PrimaryAdapterError}");
            }

            IReadOnlyList<XYZ> Expand(IReadOnlyList<PlanPoint2> polygon) => polygon
                .Select(point => origin + horizontal * point.X + vertical * point.Y)
                .ToArray();
            sections.Add(new AbutmentCoreProfileSectionEvidence
            {
                Role = profile.Role,
                StationCoordinate = profile.StationCoordinate,
                FullProfileLocal = Expand(partition.OrderedProfile),
                FootingPolygonLocal = Expand(partition.Footing),
                StemPolygonLocal = Expand(partition.Stem),
                BackwallPolygonLocal = Expand(partition.Backwall)
            });
        }

        var roles = sections.Select(section => section.Role).ToHashSet();
        if (!roles.SetEquals([
                AbutmentProfileMassKernel.ProfileStationRole.Left,
                AbutmentProfileMassKernel.ProfileStationRole.Center,
                AbutmentProfileMassKernel.ProfileStationRole.Right]))
        {
            error = "ProfileTm14V1 sections are not one unique Left/Center/Right set.";
            return false;
        }

        var ordered = sections.OrderBy(section => section.StationCoordinate).ToArray();
        var denominator = ordered[2].StationCoordinate - ordered[0].StationCoordinate;
        if (denominator <= closureTolerance)
        {
            error = "ProfileTm14V1 station separation is degenerate.";
            return false;
        }
        var fraction = (ordered[1].StationCoordinate - ordered[0].StationCoordinate) / denominator;
        // Vertex-by-vertex interpolation only means anything when the three sections carry the same
        // vertex count. The count itself is no longer pinned to 14: the elevation-band adapter
        // produces whatever the real section has, and a family with a chamfer or an extra step is
        // still a valid U abutment.
        var vertexCount = ordered[0].FullProfileLocal.Count;
        if (vertexCount < 4 || ordered.Any(section => section.FullProfileLocal.Count != vertexCount))
        {
            error = "Ba mặt cắt trạm không cùng số đỉnh nên không nội suy đối chiếu được: " +
                    string.Join(", ", ordered.Select(section =>
                        $"{section.Role}={section.FullProfileLocal.Count}")) + ".";
            return false;
        }
        var centerMismatch = Enumerable.Range(0, vertexCount).Max(index =>
        {
            var expected = ordered[0].FullProfileLocal[index] +
                           (ordered[2].FullProfileLocal[index] - ordered[0].FullProfileLocal[index]) * fraction;
            return expected.DistanceTo(ordered[1].FullProfileLocal[index]);
        });
        if (centerMismatch > interpolationTolerance)
        {
            error = $"Center section vertex mismatch " +
                    $"{UnitUtils.ConvertFromInternalUnits(centerMismatch, UnitTypeId.Millimeters):0.###} mm " +
                    $"exceeds {topology.ProfileToSolidMismatchToleranceMm:0.###} mm.";
            return false;
        }

        using var sha = SHA256.Create();
        var hashInput = $"ProfileTm14V1|{profileMassHash}|{centerMismatch:R}";
        var geometryHash = BitConverter.ToString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(hashInput)))
            .Replace("-", string.Empty, StringComparison.Ordinal);
        evidence = new AbutmentCoreProfileEvidence
        {
            Sections = ordered,
            GeometryHash = geometryHash,
            MaximumCenterInterpolationMismatch = centerMismatch
        };
        return true;
    }

    private static bool TryResolveProfileFooting(
        IReadOnlyCollection<AbutmentProfileMassEvidence> profiles,
        AbutmentLocalFrame frame,
        AbutmentTopologyProfile topology,
        AbutmentFootingBoundary solidBoundary,
        double? solidTopLocalZ,
        double? solidBottomLocalZ,
        out AbutmentFootingBoundary boundary,
        out AbutmentProfileFootingEvidence evidence,
        out AbutmentProfileSolidGateKernel.Verdict gate,
        out string error)
    {
        boundary = null!;
        evidence = null!;
        gate = AbutmentProfileSolidGateKernel.Verdict.PlanMismatch;
        error = string.Empty;
        var levelTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfilePlanarityToleranceMm, UnitTypeId.Millimeters);
        var mismatchTolerance = UnitUtils.ConvertToInternalUnits(
            topology.ProfileToSolidMismatchToleranceMm, UnitTypeId.Millimeters);
        var sections = new List<AbutmentProfileFootingKernel.StationSection>();
        foreach (var profile in profiles)
        {
            var points = profile.OrderedLoopLocal.Select(point =>
                    new AbutmentProfileMassKernel.ProfilePoint3(point.X, point.Y, point.Z))
                .ToList();
            if (!AbutmentProfileMassKernel.TryResolveFootingElevations(
                    points, levelTolerance, out var bottom, out var top))
            {
                error = $"{profile.Role}: không resolve được hai cao độ đáy/đỉnh bệ từ profile loop.";
                return false;
            }
            var bottomPoints = profile.OrderedLoopLocal
                .Where(point => Math.Abs(point.Z - bottom) <= levelTolerance)
                .OrderBy(point => point.Y)
                .ToList();
            if (bottomPoints.Count < 2)
            {
                error = $"{profile.Role}: profile không có cạnh đáy bệ hợp lệ.";
                return false;
            }
            var low = bottomPoints.First();
            var high = bottomPoints.Last();
            sections.Add(new AbutmentProfileFootingKernel.StationSection(
                profile.SourceElementIdValue,
                profile.StationCoordinate,
                new PlanPoint2(low.X, low.Y),
                new PlanPoint2(high.X, high.Y),
                bottom,
                top));
        }

        if (!AbutmentProfileFootingKernel.TryResolve(
                sections,
                new PlanPoint2(0, 1),
                mismatchTolerance,
                out var resolved,
                out error))
            return false;

        var solidLocal = solidBoundary.Vertices.Select(frame.ToLocal).ToList();
        var solidPlan = solidLocal.Select(point => new PlanPoint2(point.X, point.Y)).ToList();
        var measuredSolidBottom = solidBottomLocalZ ?? solidLocal.Average(point => point.Z);
        var measurement = AbutmentProfileSolidGateKernel.Measure(
            resolved.Boundary,
            solidPlan,
            resolved.BottomElevation,
            resolved.TopElevation,
            measuredSolidBottom,
            solidTopLocalZ,
            mismatchTolerance);
        gate = AbutmentProfileSolidGateKernel.Evaluate(
            measurement.PlanMismatch, measurement.ElevationMismatch, mismatchTolerance);
        if (gate == AbutmentProfileSolidGateKernel.Verdict.PlanMismatch)
        {
            error = AbutmentProfileSolidGateKernel.DescribePlanMismatch(measurement);
            return false;
        }

        if (!GeometryKernel.TryFindPrimaryEdgeDirections(
                resolved.Boundary,
                out var longitudinal2,
                out var transverse2,
                out var acuteAngleDeg) ||
            !GeometryKernel.TryValidateParallelogram(
                resolved.Boundary, 0.001, levelTolerance,
                out var longEdgeLength, out var endEdgeLength))
        {
            error = "Profile footing không tạo được rectangle/parallelogram hợp lệ.";
            return false;
        }

        var placement = AbutmentProfileSolidGateKernel.ResolvePlacementElevations(
            resolved.BottomElevation,
            resolved.TopElevation,
            measuredSolidBottom,
            solidTopLocalZ);
        var longitudinal = frame.Longitudinal * longitudinal2.X + frame.Transverse * longitudinal2.Y;
        var transverse = frame.Longitudinal * transverse2.X + frame.Transverse * transverse2.Y;
        longitudinal = new XYZ(longitudinal.X, longitudinal.Y, 0).Normalize();
        transverse = new XYZ(transverse.X, transverse.Y, 0).Normalize();
        if (longitudinal.DotProduct(frame.Longitudinal) < 0) longitudinal = -longitudinal;
        if (transverse.DotProduct(frame.Transverse) < 0) transverse = -transverse;
        var worldBoundary = resolved.Boundary
            .Select(point => frame.ToWorld(point.X, point.Y, placement.Bottom))
            .ToList();
        boundary = new AbutmentFootingBoundary
        {
            Vertices = worldBoundary,
            LongitudinalDirection = longitudinal,
            TransverseDirection = transverse,
            AcuteAngleDeg = acuteAngleDeg,
            LongEdgeLength = longEdgeLength,
            EndEdgeLength = endEdgeLength
        };
        evidence = new AbutmentProfileFootingEvidence
        {
            BoundaryVertices = worldBoundary,
            BottomLocalZ = resolved.BottomElevation,
            TopLocalZ = resolved.TopElevation,
            PlacementBottomLocalZ = placement.Bottom,
            PlacementTopLocalZ = placement.Top,
            CenterInterpolationMismatch = resolved.CenterMismatch,
            PlanMismatch = measurement.PlanMismatch,
            ElevationMismatch = measurement.ElevationMismatch,
            MaximumProfileToSolidMismatch = measurement.PlanMismatch,
            IsStraight = Math.Abs(acuteAngleDeg - 90) <= 1
        };
        error = gate == AbutmentProfileSolidGateKernel.Verdict.DepthMismatch
            ? AbutmentProfileSolidGateKernel.DescribeDepthMismatch(measurement)
            : AbutmentProfileSolidGateKernel.DescribeMatch(measurement);
        return true;
    }


    private const string ProfileTm14AdapterId = "ProfileTm14V1";
    private const string ElevationBandAdapterId = "ElevationBandV1";

    private readonly record struct CoreSectionPartition(
        string AdapterId,
        string PrimaryAdapterError,
        IReadOnlyList<PlanPoint2> OrderedProfile,
        IReadOnlyList<PlanPoint2> Footing,
        IReadOnlyList<PlanPoint2> Stem,
        IReadOnlyList<PlanPoint2> Backwall);

    /// <summary>
    /// Splits one station section into Footing / Stem / Backwall, trying the fixed 14-edge signature
    /// first and falling back to elevation banding.
    /// <para>
    /// Order matters: the signature adapter stays first so every family that already resolves keeps
    /// producing byte-identical regions, and the four accepted footing layers do not shift. The band
    /// adapter only sees sections the signature rejects — which today is the LEFT section of
    /// <c>MO CAU - LBH</c>, failing at the backwall return/corbel chain while Center and Right pass.
    /// </para>
    /// <para>
    /// Deliberately not preset-configurable: adding a topology field would change
    /// <c>ComputeHash</c>, break the declared <c>ruleHash</c> of the packaged profile and stop the
    /// command from opening at all.
    /// </para>
    /// </summary>
    private static bool TryPartitionSection(
        IReadOnlyList<PlanSegment2> segments,
        double closureTolerance,
        double signatureTolerance,
        double levelTolerance,
        out CoreSectionPartition partition,
        out string error)
    {
        partition = default;
        if (AbutmentCoreProfileKernel.TryResolveProfileTm14V1(
                segments, closureTolerance, signatureTolerance, out var signature, out var signatureError))
        {
            partition = new CoreSectionPartition(
                ProfileTm14AdapterId, string.Empty,
                signature.OrderedProfile,
                signature.Footing.Polygon, signature.Stem.Polygon, signature.Backwall.Polygon);
            error = string.Empty;
            return true;
        }

        var bandOptions = AbutmentCoreProfileBandKernel.DefaultOptions(
            closureTolerance, Math.Max(levelTolerance, signatureTolerance));
        if (AbutmentCoreProfileBandKernel.TryResolve(segments, bandOptions, out var band, out var bandError))
        {
            partition = new CoreSectionPartition(
                ElevationBandAdapterId, signatureError,
                band.OrderedProfile,
                band.Footing.Polygon, band.Stem.Polygon, band.Backwall.Polygon);
            error = string.Empty;
            return true;
        }

        error = $"{signatureError} | Phân dải cao độ cũng không giải được: {bandError}";
        return false;
    }

    private static double ToMm(double internalUnits) =>
        UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Millimeters);

    private static bool TryBuildFrame(FamilyInstance instance, IReadOnlyCollection<Solid> solids,
        double footingLengthMm, double footingWidthMm, out AbutmentLocalFrame frame, out string error,
        out string frameNote)
    {
        frame = null!;
        error = string.Empty;
        frameNote = string.Empty;
        var transform = instance.GetTransform();
        var vertical = transform.BasisZ;
        if (vertical.GetLength() < 0.9 || Math.Abs(vertical.Normalize().DotProduct(XYZ.BasisZ)) < 0.9)
        {
            error = "Family vertical axis is not aligned with verified model vertical.";
            return false;
        }
        vertical = vertical.DotProduct(XYZ.BasisZ) < 0 ? -vertical.Normalize() : vertical.Normalize();
        var x = RejectVertical(transform.BasisX, vertical);
        var y = RejectVertical(transform.BasisY, vertical);
        if (x.GetLength() < 1e-6 || y.GetLength() < 1e-6)
        {
            error = "Family horizontal axes are degenerate.";
            return false;
        }
        x = x.Normalize();
        y = y.Normalize();
        var origin = transform.Origin;
        var points = solids.SelectMany(Vertices).ToList();
        var xRange = Range(points, origin, x);
        var yRange = Range(points, origin, y);
        if (Math.Max(xRange, yRange) < 1e-6)
        {
            error = "Principal horizontal direction is not unique (length/depth are nearly equal).";
            return false;
        }

        // Prefer footing L/B over "longest AABB edge" — splayed wings inflate the wrong axis.
        XYZ longitudinalHint;
        XYZ transverseCandidate;
        if (footingLengthMm > 1e-3 && footingWidthMm > 1e-3)
        {
            var footingLength = UnitUtils.ConvertToInternalUnits(footingLengthMm, UnitTypeId.Millimeters);
            var footingWidth = UnitUtils.ConvertToInternalUnits(footingWidthMm, UnitTypeId.Millimeters);
            var choice = AbutmentKernel.ChooseLongitudinalAxis(
                xRange, yRange, footingLength, footingWidth,
                AbutmentKernel.DefaultAxisAmbiguityToleranceRatio,
                out var keepError, out var swapError);

            if (choice == AbutmentKernel.AbutmentAxisChoice.Swap)
            {
                // xRange matches width better when used as longitudinal → swap so y is longitudinal.
                longitudinalHint = y;
                transverseCandidate = x;
            }
            else
            {
                longitudinalHint = x;
                transverseCandidate = y;
            }

            if (choice == AbutmentKernel.AbutmentAxisChoice.Ambiguous)
            {
                // Both projected ranges exceed both declared dimensions, which makes the two errors
                // algebraically equal — the splayed-wing case this hint exists for. Measurement
                // cannot decide, so fall back to the axis the family author defined and say so.
                // The old code silently applied a third, undeclared criterion here instead.
                frameNote =
                    $"Không phân biệt được trục dọc từ số đo: giữ nguyên {ToMm(xRange):0.#} mm và " +
                    $"hoán đổi {ToMm(yRange):0.#} mm cho sai lệch bằng nhau ({ToMm(keepError):0.#} mm) " +
                    $"so với bệ khai {ToMm(footingLength):0.#} × {ToMm(footingWidth):0.#} mm — cả hai " +
                    $"phương đo được đều lớn hơn cả hai kích thước khai, đúng dấu hiệu tường cánh xoè " +
                    $"làm phình hộp bao. Dùng trục X của family làm trục dọc theo quy ước tác giả. " +
                    $"Nếu sai, sửa kích thước bệ khai trong profile hoặc dựng lại trục family.";
            }
        }
        else
        {
            if (Math.Abs(xRange - yRange) / Math.Max(xRange, yRange) < 0.02)
            {
                error = "Principal horizontal direction is not unique (length/depth are nearly equal).";
                return false;
            }
            longitudinalHint = xRange > yRange ? x : y;
            transverseCandidate = xRange > yRange ? y : x;
        }

        var weightedDirections = HorizontalEdgeDirections(solids).ToList();
        var longitudinal = longitudinalHint;
        if (AbutmentKernel.TrySelectDominantHorizontalDirection(
                weightedDirections.Select(item => new AbutmentWeightedDirection(
                    new AbutmentVector(item.Direction.X, item.Direction.Y, item.Direction.Z), item.Length)).ToList(),
                new AbutmentVector(vertical.X, vertical.Y, vertical.Z),
                new AbutmentVector(longitudinalHint.X, longitudinalHint.Y, longitudinalHint.Z),
                out var dominant, out _))
        {
            // Only accept dominant edge direction if it stays near the footing-length axis.
            var candidate = new XYZ(dominant.X, dominant.Y, dominant.Z).Normalize();
            var edgeAgreement = AbutmentAxisAgreementKernel.Classify(
                candidate.DotProduct(longitudinalHint), 0.9,
                AbutmentAxisAgreementKernel.DefaultWarningBand);
            if (edgeAgreement.Agreement == AbutmentAxisAgreementKernel.Agreement.AcceptedNearThreshold)
            {
                AbutmentDiagnostics.LogMessage(
                    AbutmentAxisAgreementKernel.NearThresholdCode,
                    AbutmentAxisAgreementKernel.DescribeNearThreshold(
                        edgeAgreement, "Hướng cạnh trội của solid so với trục dọc"));
            }
            if (edgeAgreement.Agreement != AbutmentAxisAgreementKernel.Agreement.Rejected)
                longitudinal = candidate;
        }
        var transverse = vertical.CrossProduct(longitudinal).Normalize();
        if (transverse.DotProduct(transverseCandidate) < 0) transverse = -transverse;
        longitudinal = transverse.CrossProduct(vertical).Normalize();
        frame = new AbutmentLocalFrame(origin, longitudinal, transverse, vertical);
        return true;
    }

    private static AbutmentFamilyDimensions ReadFamilyDimensions(
        FamilyInstance instance, AbutmentTopologyProfile topology)
    {
        return new AbutmentFamilyDimensions
        {
            FootingLengthMm = ReadLengthMm(instance, topology.FootingLengthParameterNames),
            FootingWidthMm = ReadLengthMm(instance, topology.FootingWidthParameterNames),
            StemTopThicknessMm = ReadLengthMm(instance, topology.StemTopThicknessParameterNames),
            StemBaseThicknessMm = ReadLengthMm(instance, topology.StemBaseThicknessParameterNames),
            WingLengthLeftMm = ReadLengthMm(instance, topology.WingLengthLeftParameterNames),
            WingLengthRightMm = ReadLengthMm(instance, topology.WingLengthRightParameterNames),
            WingLengthMm = FirstPositive(
                ReadLengthMm(instance, topology.WingLengthLeftParameterNames),
                ReadLengthMm(instance, topology.WingLengthRightParameterNames),
                ReadLengthMm(instance, topology.WingLengthParameterNames)),
            WingInnerLengthLeftMm = ReadLengthMm(instance, topology.WingInnerLengthLeftParameterNames),
            WingInnerLengthRightMm = ReadLengthMm(instance, topology.WingInnerLengthRightParameterNames),
            WingOuterLengthLeftMm = ReadLengthMm(instance, topology.WingOuterLengthLeftParameterNames),
            WingOuterLengthRightMm = ReadLengthMm(instance, topology.WingOuterLengthRightParameterNames),
            WingThicknessMm = ReadLengthMm(instance, topology.WingThicknessParameterNames),
            StemHeightLeftMm = ReadLengthMm(instance, topology.StemHeightLeftParameterNames),
            StemHeightRightMm = ReadLengthMm(instance, topology.StemHeightRightParameterNames),
            SkewAngleDeg = ReadAngleDeg(instance, topology.SkewAngleParameterNames, out var skewDeclared),
            SkewAngleDeclared = skewDeclared
        };
    }

    private static double PreferMm(double measured, double preset) =>
        measured > 1e-6 ? measured : preset;

    private static double FirstPositive(params double[] values)
    {
        foreach (var value in values)
            if (value > 1e-6) return value;
        return 0;
    }

    private static double ReadLengthMm(FamilyInstance instance, IReadOnlyCollection<string> names)
    {
        foreach (var element in ParameterSources(instance))
        foreach (var name in names)
        {
            var parameter = element.LookupParameter(name);
            if (parameter is null) continue;
            if (parameter.StorageType == StorageType.Double && parameter.HasValue)
                return UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), UnitTypeId.Millimeters);
            var text = parameter.AsValueString() ?? parameter.AsString();
            if (TryParseLocaleNumber(text, out var metersLike) && metersLike > 0)
            {
                // Family params often display meters in value string (e.g. "15,8").
                return metersLike > 50 ? metersLike : metersLike * 1000;
            }
        }
        return 0;
    }

    private static double ReadAngleDeg(
        FamilyInstance instance, IReadOnlyCollection<string> names, out bool declared)
    {
        foreach (var element in ParameterSources(instance))
        foreach (var name in names)
        {
            var parameter = element.LookupParameter(name);
            if (parameter is null) continue;
            if (parameter.StorageType == StorageType.Double && parameter.HasValue)
            {
                declared = true;
                return parameter.AsDouble() * 180 / Math.PI;
            }
            var text = parameter.AsValueString() ?? parameter.AsString();
            if (!TryParseLocaleNumber(text, out var degrees)) continue;
            declared = true;
            return degrees;
        }
        declared = false;
        return 0;
    }

    private static IEnumerable<Element> ParameterSources(FamilyInstance instance)
    {
        yield return instance;
        if (instance.Symbol is not null) yield return instance.Symbol;
    }

    private static bool TryParseLocaleNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text!.Trim()
            .Replace("°", string.Empty)
            .ToLowerInvariant()
            .Replace("mm", string.Empty)
            .Replace("m", string.Empty)
            .Trim();
        cleaned = cleaned.Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IEnumerable<(XYZ Direction, double Length)> HorizontalEdgeDirections(IEnumerable<Solid> solids)
    {
        foreach (var solid in solids)
        foreach (Edge edge in solid.Edges)
        {
            var curve = edge.AsCurve();
            var vector = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            var horizontal = new XYZ(vector.X, vector.Y, 0);
            if (horizontal.GetLength() < 1e-6) continue;
            yield return (horizontal.Normalize(), horizontal.GetLength());
        }
    }

    private static XYZ RejectVertical(XYZ value, XYZ vertical) => value - vertical * value.DotProduct(vertical);
    private static double Range(IReadOnlyCollection<XYZ> points, XYZ origin, XYZ axis)
    {
        var values = points.Select(point => (point - origin).DotProduct(axis)).ToList();
        return values.Max() - values.Min();
    }
    private static IEnumerable<XYZ> Vertices(Solid solid) => solid.Edges.Cast<Edge>()
        .SelectMany(edge => new[] { edge.AsCurve().GetEndPoint(0), edge.AsCurve().GetEndPoint(1) })
        .Distinct(new XyzComparer());
    private static AbutmentLocalBox Box(IEnumerable<XYZ> points)
    {
        var values = points.ToList();
        return new AbutmentLocalBox
        {
            Min = new XYZ(values.Min(p => p.X), values.Min(p => p.Y), values.Min(p => p.Z)),
            Max = new XYZ(values.Max(p => p.X), values.Max(p => p.Y), values.Max(p => p.Z))
        };
    }
    private static IReadOnlyList<AbutmentPlanarFaceDescriptor> DescribePlanarFaces(
        IReadOnlyList<AbutmentSolidDescriptor> solids,
        AbutmentLocalFrame frame)
    {
        var result = new List<AbutmentPlanarFaceDescriptor>();
        for (var solidIndex = 0; solidIndex < solids.Count; solidIndex++)
        {
            var faces = solids[solidIndex].Solid.Faces;
            for (var faceIndex = 0; faceIndex < faces.Size; faceIndex++)
            {
                if (faces.get_Item(faceIndex) is not PlanarFace face) continue;
                var mesh = face.Triangulate();
                var meshPoints = mesh.Vertices.ToList();
                if (meshPoints.Count == 0) continue;
                var previewTrianglesLocal = new List<XYZ>(mesh.NumTriangles * 3);
                for (var triangleIndex = 0; triangleIndex < mesh.NumTriangles; triangleIndex++)
                {
                    var triangle = mesh.get_Triangle(triangleIndex);
                    previewTrianglesLocal.Add(frame.ToLocal(triangle.get_Vertex(0)));
                    previewTrianglesLocal.Add(frame.ToLocal(triangle.get_Vertex(1)));
                    previewTrianglesLocal.Add(frame.ToLocal(triangle.get_Vertex(2)));
                }

                var normal = face.FaceNormal.Normalize();
                var horizontal = frame.Vertical.CrossProduct(normal);
                if (horizontal.GetLength() < 1e-6)
                    horizontal = frame.Longitudinal;
                else
                    horizontal = horizontal.Normalize();
                if (horizontal.DotProduct(frame.Longitudinal) < 0)
                    horizontal = -horizontal;

                var loops = new List<IReadOnlyList<XYZ>>();
                foreach (var loop in face.GetEdgesAsCurveLoops())
                {
                    var points = new List<XYZ>();
                    foreach (var curve in loop)
                    foreach (var point in curve.Tessellate())
                    {
                        if (points.Count == 0 || points[^1].DistanceTo(point) > 1e-8)
                            points.Add(point);
                    }
                    if (points.Count > 2 && points[0].DistanceTo(points[^1]) <= 1e-8)
                        points.RemoveAt(points.Count - 1);
                    if (points.Count >= 3) loops.Add(points);
                }
                if (loops.Count == 0) continue;

                result.Add(new AbutmentPlanarFaceDescriptor
                {
                    HostId = solids[solidIndex].HostId,
                    SolidIndex = solidIndex,
                    FaceIndex = faceIndex,
                    Face = face,
                    LocalBox = Box(meshPoints.Select(frame.ToLocal)),
                    Origin = face.Origin,
                    Normal = normal,
                    HorizontalDirection = horizontal,
                    BoundaryLoops = loops,
                    PreviewTrianglesLocal = previewTrianglesLocal,
                    Area = face.Area
                });
            }
        }
        return result;
    }



    /// <summary>
    /// Measures every upward horizontal band of the host and hands them to the band kernel.
    /// Declared depth is only a stem-distance cap, never a target window, so a modelled 1800 mm
    /// footing still resolves against a drawing that says 2000 mm.
    /// </summary>
    private static bool TryResolveFootingTop(
        IReadOnlyList<Solid> solids,
        AbutmentLocalFrame frame,
        AbutmentFootingBoundary boundary,
        double expectedDepth,
        out AbutmentFootingBandKernel.FootingTopResolution resolution,
        out AbutmentFootingBandKernel.FootingTopScan scan,
        out string error)
    {
        resolution = default;
        scan = default;
        error = string.Empty;
        var localBoundary = boundary.Vertices.Select(frame.ToLocal).ToList();
        var soffit = localBoundary.Min(point => point.Z);
        var bottomPolygon = localBoundary
            .Select(point => new PlanPoint2(point.X, point.Y))
            .ToList();
        var bottomArea = Math.Abs(GeometryKernel.SignedArea(bottomPolygon));
        if (bottomArea <= 1e-9)
        {
            error = "Soffit boundary không có diện tích hợp lệ.";
            return false;
        }

        var vertical = frame.Vertical.Normalize();
        var mergeTolerance = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
        var scannedFaces = solids
            .SelectMany(solid => solid.Faces.Cast<Face>().OfType<PlanarFace>())
            .Select(face => new
            {
                Face = face,
                Direction = face.FaceNormal.Normalize().DotProduct(vertical),
                Elevation = frame.ToLocal(face.Origin).Z
            })
            .ToList();
        var upwardFaces = scannedFaces
            .Where(item => item.Direction >= 1 - 1e-3)
            .ToList();
        var measuredFaces = upwardFaces
            .Where(item => item.Elevation > soffit + mergeTolerance)
            .ToList();
        var levels = GeometryService.UniqueCoordinates(
                measuredFaces.Select(item => item.Elevation),
                mergeTolerance)
            .OrderBy(level => level)
            .ToList();
        var bands = levels
            .Select(level => new AbutmentFootingBandKernel.HorizontalFaceBand(
                level,
                measuredFaces
                    .Where(item => Math.Abs(item.Elevation - level) <= mergeTolerance)
                    .Sum(item => item.Face.Area)))
            .ToList();

        scan = new AbutmentFootingBandKernel.FootingTopScan(
            solids.Count, scannedFaces.Count, upwardFaces.Count, bottomArea);
        return AbutmentFootingBandKernel.TryResolveTop(
            soffit,
            bottomArea * 0.02,
            bands,
            scan,
            out resolution,
            out error,
            AbutmentFootingBandKernel.ReasonableRiseCap(expectedDepth));
    }
    private IReadOnlyList<Element> ResolveComponentHosts(
        Element primary,
        AbutmentHostAssemblyStrategy strategy)
    {
        if (strategy == AbutmentHostAssemblyStrategy.SingleHost)
            return [primary];
        if (primary.AssemblyInstanceId == ElementId.InvalidElementId ||
            _document.GetElement(primary.AssemblyInstanceId) is not AssemblyInstance assembly)
            return [];

        var members = assembly.GetMemberIds()
            .Select(_document.GetElement)
            .Where(element => element is not null &&
                              element.Category?.Id == new ElementId(BuiltInCategory.OST_GenericModel) &&
                              RebarHostData.IsValidHost(element) &&
                              HasConcreteEvidence(element))
            .Cast<Element>()
            .OrderBy(element => element.Id == primary.Id ? 0 : 1)
            .ThenBy(element => ElementIdCompat.ToLong(element.Id))
            .ToList();
        return members.Any(element => element.Id == primary.Id) ? members : [];
    }


    private static string HashGeometry(
        IEnumerable<AbutmentSolidDescriptor> solids,
        IReadOnlyList<AbutmentPlanarFaceDescriptor> planarFaces,
        AbutmentFootingBoundary? footingBoundary,
        IReadOnlyList<AbutmentPileExclusion> piles,
        string profileMassHash = "",
        string wingProfileHash = "")
    {
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var solidRows = solids.Select(item =>
                string.Join(",", ElementIdCompat.ToLong(item.HostId),
                    F(item.LocalBox.Min.X), F(item.LocalBox.Min.Y), F(item.LocalBox.Min.Z),
                    F(item.LocalBox.Max.X), F(item.LocalBox.Max.Y), F(item.LocalBox.Max.Z), F(item.Volume)))
            .OrderBy(row => row, StringComparer.Ordinal);
        var text = string.Join("|", solidRows);
        text += "|PLANAR_FACES|" + AbutmentGeometryHashKernel.SerializePlanarFaces(
            planarFaces.Select(face => new AbutmentPlanarFaceHashInput(
                ElementIdCompat.ToLong(face.HostId),
                face.Origin.X, face.Origin.Y, face.Origin.Z,
                face.Normal.X, face.Normal.Y, face.Normal.Z,
                face.Area,
                face.BoundaryLoops
                    .SelectMany(loop => loop)
                    .Select(point => new AbutmentHashPoint3(point.X, point.Y, point.Z))
                    .ToList())));
        if (footingBoundary is not null)
        {
            text += "|FOOTING|" + string.Join("|", footingBoundary.Vertices.Select(point =>
                string.Join(",", F(point.X), F(point.Y), F(point.Z))));
            text += "|" + string.Join(",", F(footingBoundary.LongitudinalDirection.X),
                F(footingBoundary.LongitudinalDirection.Y),
                F(footingBoundary.TransverseDirection.X),
                F(footingBoundary.TransverseDirection.Y),
                F(footingBoundary.AcuteAngleDeg),
                F(footingBoundary.LongEdgeLength),
                F(footingBoundary.EndEdgeLength));
        }
        text += "|PILES|" + AbutmentGeometryHashKernel.SerializePiles(piles.Select(pile =>
            new AbutmentPileHashInput(
                ElementIdCompat.ToLong(pile.SourceId), pile.Center.X, pile.Center.Y,
                pile.MinZ, pile.MaxZ, pile.PileRadius, pile.Clearance)));
        if (!string.IsNullOrWhiteSpace(profileMassHash))
            text += "|PROFILE_MASS|" + profileMassHash.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(wingProfileHash))
            text += "|WING_PROFILE|" + wingProfileHash.Trim().ToUpperInvariant();
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty);
    }


    private sealed class XyzComparer : IEqualityComparer<XYZ>
    {
        public bool Equals(XYZ? a, XYZ? b) => a is not null && b is not null && a.IsAlmostEqualTo(b);
        public int GetHashCode(XYZ value)
        {
            unchecked
            {
                var hash = Math.Round(value.X, 6).GetHashCode();
                hash = hash * 397 ^ Math.Round(value.Y, 6).GetHashCode();
                return hash * 397 ^ Math.Round(value.Z, 6).GetHashCode();
            }
        }
    }
}
