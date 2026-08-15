using System.Globalization;
using System.Security.Cryptography;
using System.Text;
#if !PROFILE_PLANNER_TESTS
using BIM.DatViet.Infrastructure;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Recognition;
using BIM.DatViet.RevitServices;
#endif
using BIM.DatViet.Models;
using BIM.DatViet.Domain;

namespace BIM.DatViet.Planning;

#if !PROFILE_PLANNER_TESTS
public sealed class AbutmentRebarPlanner
{
    /// <summary>
    /// Smallest accepted |cos| between the station measurement direction and the local
    /// transverse axis. Covers skews up to 60 degrees; beyond that the toe/heel edges no
    /// longer describe the measurement direction and the datum must fail closed.
    /// </summary>
    private const double StationDatumAxisAlignmentMinimum = 0.5;

    private readonly Document _document;
    private readonly RebarTypeCatalog _catalog;
    private static readonly IAbutmentRuleGeometryStrategy ProfileFirstFootingMatStrategy =
        new ProfileFirstFootingMatGeometryStrategy();
    private static readonly IAbutmentRuleGeometryStrategy ProfileClippedSurfaceStrategy =
        new ProfileClippedSurfaceGeometryStrategy();
    private static readonly IAbutmentRuleGeometryStrategy SolidClippedSurfaceStrategy =
        new SolidClippedSurfaceGeometryStrategy();

    public AbutmentRebarPlanner(Document document)
    {
        _document = document;
        _catalog = new RebarTypeCatalog(document);
    }

    public AbutmentRebarPlan Build(AbutmentClassification classification, AbutmentRebarPresetV1 preset)
    {
        if (classification.Status != AbutmentResolutionStatus.Resolved || classification.Context is null)
            throw new InvalidOperationException("Cannot plan rebar from unresolved abutment classification.");
        var context = classification.Context;
        var normalizedPreset = preset.Clone();
        normalizedPreset.RuleHash = normalizedPreset.ComputeHash();
        var plan = new AbutmentRebarPlan
        {
            Context = context,
            Preset = normalizedPreset,
            GeometryHash = context.GeometryHash
        };
        plan.Zones.AddRange(classification.Zones);
        plan.Issues.AddRange(classification.Issues);
        foreach (var issue in normalizedPreset.EvidenceLoadIssues)
            plan.Issues.Add(new ValidationIssue(issue.Code,
                issue.IsError ? ValidationSeverity.Error : ValidationSeverity.Warning,
                issue.Message, context.Host.Id));
        foreach (var issue in normalizedPreset.Validate())
            plan.Issues.Add(new ValidationIssue(issue.Code,
                issue.IsError ? ValidationSeverity.Error : ValidationSeverity.Warning,
                issue.Message, context.Host.Id));
        if (context.Piles.Count > 0 && normalizedPreset.UsesPilePlacementMargin)
            plan.Issues.Add(new ValidationIssue(
                "ABUTMENT_PILE_PLACEMENT_MARGIN",
                ValidationSeverity.Info,
                $"Planner dùng thêm {normalizedPreset.Topology.PilePlacementMarginMm:0.#} mm ngoài clear cọc " +
                "để bù sai số placement/readback.",
                context.Host.Id));

        foreach (var rule in normalizedPreset.Rules
                     .Where(item => item.Enabled)
                     .OrderBy(item => item.Zone)
                     .ThenBy(item => item.FaceRole)
                     .ThenBy(item => item.LayerOrder)
                     .ThenBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase))
        {
            var zone = classification.Zones.SingleOrDefault(item => item.Kind == rule.Zone);
            if (zone is null)
            {
                plan.Issues.Add(new ValidationIssue("ABUTMENT_RULE_ZONE_MISSING", ValidationSeverity.Error,
                    $"Rule '{rule.RuleId}' references unresolved zone '{rule.Zone}'.", context.Host.Id));
                continue;
            }
            if (!TryResolveBarType(
                    rule,
                    out var type,
                    out var typeErrorCode,
                    out var typeError))
            {
                plan.Issues.Add(new ValidationIssue(typeErrorCode, ValidationSeverity.Error,
                    $"{rule.RuleId}: {typeError}"));
                continue;
            }
            var typeDiameterMm = UnitUtils.ConvertFromInternalUnits(type.Diameter, UnitTypeId.Millimeters);
            if (Math.Abs(typeDiameterMm - rule.DiameterMm) > 0.5)
            {
                plan.Issues.Add(new ValidationIssue("REBAR_BAR_TYPE_DIAMETER_MISMATCH", ValidationSeverity.Error,
                    $"Rule '{rule.RuleId}' expects D{rule.DiameterMm:0.#}, but type '{type.Name}' is D{typeDiameterMm:0.#}."));
                continue;
            }
            if (!TryResolveCover(context, zone, rule, out var cover, out var coverError))
            {
                plan.Issues.Add(new ValidationIssue("REBAR_COVER_UNRESOLVED", ValidationSeverity.Error,
                    $"{rule.RuleId}: {coverError}", context.Host.Id));
                continue;
            }
            var centerline = cover + type.Diameter / 2;
            var layerDepth = ResolveLayerDepth(
                plan,
                rule,
                UnitUtils.ConvertFromInternalUnits(cover, UnitTypeId.Millimeters),
                typeDiameterMm);
            if (!TryBuildRuleBars(context, zone, rule, normalizedPreset,
                    centerline, layerDepth, type, out var bars, out var error))
            {
                plan.Issues.Add(new ValidationIssue("ABUTMENT_BAR_GEOMETRY_UNRESOLVED", ValidationSeverity.Error,
                    $"{rule.RuleId}: {error}", context.Host.Id));
                continue;
            }
            plan.Bars.AddRange(bars);
        }
        ValidateFootingLayerSeparation(plan);
        DetectDuplicates(plan);
        plan.PlanHash = ComputePlanHash(plan);
        return plan;
    }

    private bool TryResolveBarType(
        AbutmentZoneRule rule,
        out RebarTypeInfo type,
        out string errorCode,
        out string error)
    {
        errorCode = "REBAR_BAR_TYPE_NOT_FOUND";
        if (_catalog.TryResolve(rule.BarTypeName, rule.DiameterMm, out type, out error))
        {
            var gradeAssessment = AbutmentBarTypeGradeKernel.Assess(
                type.MinimumYieldStrengthMpa,
                rule.MinimumYieldStrengthMpa);
            if (gradeAssessment == AbutmentBarTypeGradeAssessment.MetadataMissing)
            {
                errorCode = "REBAR_BAR_TYPE_GRADE_METADATA_MISSING";
                error =
                    $"RebarBarType '{type.Name}' chưa có Material/Structural Asset nên không đọc được fy; " +
                    $"rule yêu cầu mác {rule.RequiredBarGrade} với fy ≥ " +
                    $"{rule.MinimumYieldStrengthMpa:0.#} MPa. Gán Structural Asset cho type này trong Revit " +
                    "rồi Phân tích lại.";
                type = null!;
                return false;
            }
            if (gradeAssessment == AbutmentBarTypeGradeAssessment.BelowRequired)
            {
                errorCode = "REBAR_BAR_TYPE_GRADE_BELOW_REQUIRED";
                error =
                    $"RebarBarType '{type.Name}' dùng material '{type.MaterialName}' có fy " +
                    $"{type.MinimumYieldStrengthMpa:0.#} MPa, thấp hơn mác {rule.RequiredBarGrade} " +
                    $"yêu cầu fy ≥ {rule.MinimumYieldStrengthMpa:0.#} MPa.";
                type = null!;
                return false;
            }
            // Preserve the reviewed rule as immutable configuration. The actual
            // fallback type name/id is recorded on PlannedAbutmentRebar.
            return true;
        }

        type = null!;
        return false;
    }

    private bool TryResolveCover(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        out double cover,
        out string error)
    {
        cover = 0;
        error = string.Empty;
        if (rule.Zone == AbutmentZoneKind.Footing && IsFootingMatTemplate(rule.GeometryTemplate))
        {
            if (rule.FootingCover.FaceMm is not > 0)
            {
                error = "Footing mat thiếu face cover đã được duyệt.";
                return false;
            }
            cover = UnitUtils.ConvertToInternalUnits(
                rule.FootingCover.FaceMm.Value, UnitTypeId.Millimeters);
            return true;
        }
        var overrideCover = rule.CoverOverrideMm is > 0
            ? UnitUtils.ConvertToInternalUnits(rule.CoverOverrideMm.Value, UnitTypeId.Millimeters)
            : (double?)null;
        var measuredFace = MeasuredFace(zone, rule.FaceRole);
        var coverRole = measuredFace is null
            ? rule.FaceRole
            : CoverRoleFromNormal(context, measuredFace.Normal);
        double? hostCover = measuredFace is null
            ? TryResolveFaceCover(context, coverRole)
            : TryResolveMeasuredFaceCover(measuredFace) ??
              TryResolveFaceCover(context, coverRole);
        var coverHost = measuredFace is null
            ? context.Host
            : _document.GetElement(measuredFace.HostId) ?? context.Host;
        if (hostCover is null)
        {
            var ids = PreferCoverParameters(coverRole);
            foreach (var id in ids)
            {
                if (!TryGetCoverDistance(coverHost.get_Parameter(id), out var distance)) continue;
                hostCover = distance;
                break;
            }
        }

        if (AbutmentKernel.TryResolveCover(overrideCover, hostCover, out cover, out _)) return true;
        error = $"Không có cover override hoặc Rebar cover type hợp lệ cho mặt {coverRole}.";
        return false;
    }

    private double? TryResolveMeasuredFaceCover(AbutmentPlanarFaceDescriptor descriptor)
    {
        var host = _document.GetElement(descriptor.HostId);
        if (host is null) return null;
        var hostData = RebarHostData.GetRebarHostData(host);
        if (hostData is null) return null;
        IList<Reference>? exposedFaces;
        try { exposedFaces = hostData.GetExposedFaces(); }
        catch { return null; }
        if (exposedFaces is null) return null;
        var planeTolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        foreach (var faceReference in exposedFaces)
        {
            try
            {
                if (host.GetGeometryObjectFromReference(faceReference) is not PlanarFace face) continue;
                if (face.FaceNormal.Normalize().DotProduct(descriptor.Normal.Normalize()) < 0.999) continue;
                var planeDistance = Math.Abs(
                    (face.Origin - descriptor.Origin).DotProduct(descriptor.Normal.Normalize()));
                if (planeDistance > planeTolerance) continue;
                var coverType = hostData.GetCoverType(faceReference);
                if (coverType is not null && coverType.CoverDistance > 0)
                    return coverType.CoverDistance;
            }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException)
            {
                return null;
            }
        }
        return null;
    }

    private static AbutmentPlanarFaceDescriptor? MeasuredFace(
        AbutmentZoneDescriptor zone,
        AbutmentFaceRole role) =>
        role switch
        {
            AbutmentFaceRole.Front => zone.SurfacePair?.FrontFace,
            AbutmentFaceRole.Back => zone.SurfacePair?.BackFace,
            AbutmentFaceRole.Inner => zone.SurfacePair?.InnerFace,
            AbutmentFaceRole.Outer => zone.SurfacePair?.OuterFace,
            _ => null
        };
    private static AbutmentFaceRole CoverRoleFromNormal(
        AbutmentHostContext context,
        XYZ normal)
    {
        normal = normal.Normalize();
        var vertical = normal.DotProduct(context.Frame.Vertical);
        var transverse = normal.DotProduct(context.Frame.Transverse);
        var longitudinal = normal.DotProduct(context.Frame.Longitudinal);
        if (Math.Abs(vertical) >= Math.Abs(transverse) &&
            Math.Abs(vertical) >= Math.Abs(longitudinal))
            return vertical >= 0 ? AbutmentFaceRole.Top : AbutmentFaceRole.Bottom;
        if (Math.Abs(transverse) >= Math.Abs(longitudinal))
            return transverse >= 0 ? AbutmentFaceRole.Back : AbutmentFaceRole.Front;
        return longitudinal >= 0 ? AbutmentFaceRole.EndEnd : AbutmentFaceRole.StartEnd;
    }


    private double? TryResolveFaceCover(AbutmentHostContext context, AbutmentFaceRole role)
    {
        var hostData = RebarHostData.GetRebarHostData(context.Host);
        if (hostData is null) return null;
        IList<Reference>? faces;
        try { faces = hostData.GetExposedFaces(); }
        catch { return null; }
        if (faces is null || faces.Count == 0) return null;

        var desired = role switch
        {
            AbutmentFaceRole.Bottom => -context.Frame.Vertical,
            AbutmentFaceRole.Top => context.Frame.Vertical,
            AbutmentFaceRole.Front => -context.Frame.Transverse,
            AbutmentFaceRole.Back => context.Frame.Transverse,
            AbutmentFaceRole.StartEnd => -context.Frame.Longitudinal,
            AbutmentFaceRole.EndEnd => context.Frame.Longitudinal,
            _ => XYZ.Zero
        };
        if (desired.IsZeroLength()) return null;
        desired = desired.Normalize();

        double? bestCover = null;
        var bestScore = 0.85;
        foreach (var faceRef in faces)
        {
            try
            {
                if (context.Host.GetGeometryObjectFromReference(faceRef) is not Face face) continue;
                var bounds = face.GetBoundingBox();
                var sample = new UV(
                    (bounds.Min.U + bounds.Max.U) / 2,
                    (bounds.Min.V + bounds.Max.V) / 2);
                var normal = face.ComputeNormal(sample).Normalize();
                var score = normal.DotProduct(desired);
                if (score <= bestScore) continue;
                var coverType = hostData.GetCoverType(faceRef);
                if (coverType is null || coverType.CoverDistance <= 0) continue;
                bestScore = score;
                bestCover = coverType.CoverDistance;
            }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException)
            {
                // A stale face is not accepted; the role must resolve from another live face or override.
            }
        }
        return bestCover;
    }

    private static BuiltInParameter[] PreferCoverParameters(AbutmentFaceRole role) => role switch
    {
        AbutmentFaceRole.Bottom
            => [BuiltInParameter.CLEAR_COVER_BOTTOM, BuiltInParameter.CLEAR_COVER_OTHER,
                BuiltInParameter.CLEAR_COVER],
        AbutmentFaceRole.Top
            => [BuiltInParameter.CLEAR_COVER_TOP, BuiltInParameter.CLEAR_COVER_OTHER,
                BuiltInParameter.CLEAR_COVER],
        AbutmentFaceRole.Front
            => [BuiltInParameter.CLEAR_COVER_EXTERIOR, BuiltInParameter.CLEAR_COVER_OTHER,
                BuiltInParameter.CLEAR_COVER],
        AbutmentFaceRole.Back
            => [BuiltInParameter.CLEAR_COVER_INTERIOR, BuiltInParameter.CLEAR_COVER_OTHER,
                BuiltInParameter.CLEAR_COVER],
        _ => [BuiltInParameter.CLEAR_COVER_OTHER, BuiltInParameter.CLEAR_COVER]
    };

    private bool TryGetCoverDistance(Parameter? parameter, out double distance)
    {
        distance = 0;
        if (parameter?.StorageType != StorageType.ElementId) return false;
        if (_document.GetElement(parameter.AsElementId()) is not RebarCoverType coverType ||
            coverType.CoverDistance <= 0) return false;
        distance = coverType.CoverDistance;
        return true;
    }

    /// <summary>
    /// Depth of the bar centroid below the face this layer is covered against, in internal units.
    /// The arithmetic belongs to <see cref="AbutmentMatLayerElevationKernel"/> so the elevation the
    /// drawing actually dimensions — the centroid, not the clear cover — can be asserted offline.
    /// </summary>
    private static double ResolveLayerDepth(
        AbutmentRebarPlan plan,
        AbutmentZoneRule rule,
        double faceCoverMm,
        double diameterMm)
    {
        var previous = plan.Bars
            .Where(bar => bar.Zone == rule.Zone &&
                          bar.Rule.FaceRole == rule.FaceRole &&
                          bar.Rule.LayerOrder < rule.LayerOrder)
            .OrderByDescending(bar => bar.Rule.LayerOrder)
            .FirstOrDefault();
        if (AbutmentMatLayerElevationKernel.TryResolveCentroidDepthFromFace(
                faceCoverMm,
                diameterMm,
                previous is null
                    ? null
                    : UnitUtils.ConvertFromInternalUnits(previous.LayerDepth, UnitTypeId.Millimeters),
                previous?.Rule.DiameterMm,
                rule.ClearGapToPreviousMm,
                out var centroidDepthMm,
                out var error))
            return UnitUtils.ConvertToInternalUnits(centroidDepthMm, UnitTypeId.Millimeters);

        plan.Issues.Add(new ValidationIssue("ABUTMENT_MAT_LAYER_DEPTH_UNRESOLVED",
            ValidationSeverity.Error, $"{rule.RuleId}: {error}", plan.Context.Host.Id));
        return UnitUtils.ConvertToInternalUnits(faceCoverMm + diameterMm / 2, UnitTypeId.Millimeters);
    }

    private static bool TryBuildRuleBars(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        AbutmentRebarPresetV1 preset,
        double centerlineOffset,
        double layerDepth,
        RebarTypeInfo type,
        out List<PlannedAbutmentRebar> bars,
        out string error)
    {
        // Thép chờ rải theo bước tối đa dọc chân thân nên không đi qua cổng StationSchedule: chuỗi
        // ga đo từ bản vẽ là khái niệm của thảm bệ, không phải của lồng thép chờ.
        if (IsStarterTemplate(rule.GeometryTemplate))
        {
            if (rule.LayoutRule != AbutmentLayoutRule.MaximumSpacing)
            {
                bars = [];
                error = $"ABUTMENT_STARTER_LAYOUT_UNSUPPORTED: {rule.RuleId} là thép chờ nên phải " +
                        $"dùng MaximumSpacing, không phải {rule.LayoutRule}.";
                return false;
            }
            return TryBuildStarterDowels(
                context, zone, rule, preset, centerlineOffset, layerDepth, type, out bars, out error);
        }

        if (rule.DistributionRepresentation != AbutmentDistributionRepresentation.ExplicitStations ||
            rule.LayoutRule != AbutmentLayoutRule.StationSchedule)
        {
            bars = [];
            error = "Runtime production chỉ hỗ trợ StationSchedule dạng ExplicitStations.";
            return false;
        }

        if (rule.Zone == AbutmentZoneKind.Footing && IsFootingMatTemplate(rule.GeometryTemplate))
            return ProfileFirstFootingMatStrategy.TryBuild(
                context, zone, rule, preset, centerlineOffset, layerDepth, type,
                out bars, out error);

        if (IsSurfaceFaceTemplate(rule.GeometryTemplate))
        {
            var strategy = AbutmentProfileSurfacePathKernel.SelectStrategy(
                rule.Zone, rule.GeometryTemplate);
            if (strategy == AbutmentProfileSurfacePathKernel.StrategyKind.ProfileAuthoritative)
                return ProfileClippedSurfaceStrategy.TryBuild(
                    context, zone, rule, preset, centerlineOffset, layerDepth, type,
                    out bars, out error);
            if (strategy == AbutmentProfileSurfacePathKernel.StrategyKind.SolidSurface)
                return SolidClippedSurfaceStrategy.TryBuild(
                    context, zone, rule, preset, centerlineOffset, layerDepth, type,
                    out bars, out error);
        }

        bars = [];
        error = $"Chưa có strategy runtime cho zone '{rule.Zone}' / template '{rule.GeometryTemplate}'.";
        return false;
    }

    /// <summary>
    /// Adapter mỏng giữa <see cref="AbutmentStarterBarKernel"/> và Revit: đọc chân thân từ ba trạm
    /// PROFILE_TM, gọi kernel thuần, rồi đổi kết quả về toạ độ thế giới.
    /// <para>
    /// Chiều dày bệ lấy từ <c>PlacementTopLocalZ − PlacementBottomLocalZ</c> — cao độ <b>đo được</b>
    /// — chứ không lấy <c>topology.FootingDepthMm</c>: bản vẽ khai 2000 mm trong khi family dựng
    /// 1800 mm, và thanh chờ đâm thủng đáy bệ là lỗi không được phép làm tròn cho qua.
    /// </para>
    /// </summary>
    private static bool TryBuildStarterDowels(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        AbutmentRebarPresetV1 preset,
        double centerlineOffset,
        double layerDepth,
        RebarTypeInfo type,
        out List<PlannedAbutmentRebar> bars,
        out string error)
    {
        bars = [];
        error = string.Empty;
        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);

        if (context.CoreProfile is not { Sections.Count: >= 2 } core)
        {
            error = $"ABUTMENT_STARTER_CORE_PROFILE_REQUIRED: {rule.RuleId} cần mặt cắt lõi " +
                    "PROFILE_TM để đọc chân thân; hiện chưa phân được Bệ/Thân/Tường đỉnh.";
            return false;
        }
        if (context.ProfileFooting is not { } footing)
        {
            error = $"ABUTMENT_STARTER_FOOTING_REQUIRED: {rule.RuleId} cần cao độ bệ đã chốt để " +
                    "kẹp chiều sâu neo.";
            return false;
        }

        var sections = new List<AbutmentStarterBarKernel.StemBaseSection>();
        foreach (var section in core.Sections)
        {
            if (section.StemPolygonLocal.Count < 3) continue;
            var stationLocal = context.Frame.ToLocal(section.StemPolygonLocal[0]).X;
            var transverse = section.StemPolygonLocal
                .Select(point => context.Frame.ToLocal(point).Y).ToList();
            sections.Add(new AbutmentStarterBarKernel.StemBaseSection(
                stationLocal / mm,
                transverse.Min() / mm,
                transverse.Max() / mm,
                footing.PlacementTopLocalZ / mm));
        }

        var request = new AbutmentStarterBarKernel.Request(
            Sections: sections,
            Face: rule.FaceRole == AbutmentFaceRole.Back
                ? AbutmentStarterBarKernel.FacePosition.Back
                : AbutmentStarterBarKernel.FacePosition.Front,
            CoverPlusHalfDiameter: centerlineOffset / mm,
            MaximumSpacing: rule.SpacingMm,
            StartClearance: rule.StartStationClearanceMm,
            EndClearance: rule.EndStationClearanceMm,
            IncludeFirst: rule.IncludeFirst,
            IncludeLast: rule.IncludeLast,
            EmbedmentDepth: rule.Development.RequiredLengthMm,
            Projection: rule.LapSplice?.LapLengthMm ?? rule.Development.RequiredLengthMm,
            FootingDepth: (footing.PlacementTopLocalZ - footing.PlacementBottomLocalZ) / mm,
            BottomLayerDepth: layerDepth / mm);

        if (!AbutmentStarterBarKernel.TryBuild(request, out var resolved, out error)) return false;

        var index = 0;
        foreach (var bar in resolved.Bars)
        {
            index++;
            bars.Add(new PlannedAbutmentRebar
            {
                Key = $"{rule.RuleId}#{index}",
                ZoneCode = rule.ZoneCode,
                Zone = rule.Zone,
                HostId = context.Host.Id,
                Rule = rule,
                ResolvedBarTypeId = type.Id,
                ResolvedBarTypeName = type.Name,
                Points = bar
                    .Select(point => context.Frame.ToWorld(
                        point.Longitudinal * mm, point.Transverse * mm, point.Vertical * mm))
                    .ToList(),
                PlaneNormal = context.Frame.Longitudinal,
                DistributionRepresentation = rule.DistributionRepresentation,
                DistributionLength = 0,
                Spacing = resolved.ActualSpacing * mm,
                PlannedQuantity = 1,
                Cover = centerlineOffset - type.Diameter / 2,
                CenterlineOffset = centerlineOffset,
                LayerDepth = layerDepth,
                PositionIndex = index,
                PieceIndex = 1,
                PieceCount = 1,
                PieceLengthMm = request.EmbedmentDepth + request.Projection,
                PositionLengthMm = request.EmbedmentDepth + request.Projection,
                LapLengthMm = 0,
                ReversedLayout = false
            });
        }

        return bars.Count > 0;
    }

    /// <summary>
    /// Thép chờ nối bệ với thân. Chỉ <c>StarterLongitudinal</c> có strategy runtime đợt này;
    /// <c>StarterTransverse</c> vẫn nằm ngoài whitelist schema.
    /// </summary>
    private static bool IsStarterTemplate(AbutmentGeometryTemplate template) =>
        template is AbutmentGeometryTemplate.StarterLongitudinal;

    private static bool IsFootingMatTemplate(AbutmentGeometryTemplate template) =>
        template is AbutmentGeometryTemplate.BottomLongitudinal
            or AbutmentGeometryTemplate.BottomTransverse
            or AbutmentGeometryTemplate.TopLongitudinal
            or AbutmentGeometryTemplate.TopTransverse;

    private static bool IsSurfaceFaceTemplate(AbutmentGeometryTemplate template) =>
        template is AbutmentGeometryTemplate.VerticalFront
            or AbutmentGeometryTemplate.VerticalBack
            or AbutmentGeometryTemplate.HorizontalFront
            or AbutmentGeometryTemplate.HorizontalBack
            or AbutmentGeometryTemplate.VerticalInner
            or AbutmentGeometryTemplate.VerticalOuter
            or AbutmentGeometryTemplate.HorizontalInner
            or AbutmentGeometryTemplate.HorizontalOuter;

    private static bool TryBuildProfileFirstMat(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        AbutmentRebarPresetV1 preset,
        double centerlineOffset,
        double layerDepth,
        RebarTypeInfo type,
        out List<PlannedAbutmentRebar> bars,
        out string error)
    {
        var topology = preset.Topology;
        bars = [];
        error = string.Empty;
        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        const double spacing = 0;

        if (topology.RequireProfileMassMarkers && context.ProfileFooting is null)
        {
            error = "ABUTMENT_PROFILE_FOOTING_REQUIRED: A1/F1 requires validated L/Center/R station sections.";
            return false;
        }

        var boundary = context.FootingBoundary;
        if (boundary is null || !boundary.IsValid)
        {
            error = "ABUTMENT_PROFILE_FOOTING_UNRESOLVED: mat requires a validated PROFILE_TM rectangle/parallelogram boundary.";
            return false;
        }

        var polygon = boundary.Vertices.Select(point => new PlanPoint2(point.X, point.Y)).ToList();
        var barRadius = type.Diameter / 2;
        if (!TryBuildFootingCenterlineBoundary(
                polygon, boundary, rule.FootingCover, barRadius,
                out var centerlineBoundary, out var edgeInset, out error))
            return false;
        var layerLocalZ = rule.FaceRole == AbutmentFaceRole.Bottom
            ? zone.LocalBox.Min.Z + layerDepth
            : zone.LocalBox.Max.Z - layerDepth;
        var layerWorldZ = context.Frame.ToWorld(0, 0, layerLocalZ).Z;

        var alongLongitudinal = rule.GeometryTemplate is AbutmentGeometryTemplate.BottomLongitudinal
            or AbutmentGeometryTemplate.TopLongitudinal;
        var direction3 = alongLongitudinal
            ? boundary.LongitudinalDirection
            : boundary.TransverseDirection;
        direction3 = new XYZ(direction3.X, direction3.Y, 0).Normalize();
        var direction = new PlanPoint2(direction3.X, direction3.Y);
        var stationNormal = new PlanPoint2(-direction.Y, direction.X);
        IReadOnlyList<double> normalOffsetsMm;
        try
        {
            normalOffsetsMm = rule.StationSchedule.ReferenceAxis switch
            {
                AbutmentStationReferenceAxis.LongitudinalBoundary =>
                    GeometryKernel.ProjectOffsetsNormalToBar(
                        rule.StationSchedule.CenterlineOffsetsMm,
                        direction,
                        new PlanPoint2(
                            boundary.LongitudinalDirection.X,
                            boundary.LongitudinalDirection.Y)),
                AbutmentStationReferenceAxis.TransverseBoundary =>
                    GeometryKernel.ProjectOffsetsNormalToBar(
                        rule.StationSchedule.CenterlineOffsetsMm,
                        direction,
                        new PlanPoint2(
                            boundary.TransverseDirection.X,
                            boundary.TransverseDirection.Y)),
                _ => rule.StationSchedule.CenterlineOffsetsMm
            };
        }
        catch (InvalidOperationException exception)
        {
            error = $"ABUTMENT_STATION_REFERENCE_INVALID: {exception.Message}";
            return false;
        }
        var concreteMinimum = polygon.Min(point => point.Dot(stationNormal));
        var concreteMaximum = polygon.Max(point => point.Dot(stationNormal));
        var concreteSpanMm = UnitUtils.ConvertFromInternalUnits(
            concreteMaximum - concreteMinimum, UnitTypeId.Millimeters);
        if (rule.StationSchedule.StationLayout is not null &&
            !TryCrossCheckStationLayout(
                context, rule, topology, boundary, direction, normalOffsetsMm, concreteSpanMm,
                out error))
            return false;
        if (!TryAnchorStationDatum(
                context, rule, topology, alongLongitudinal, stationNormal,
                normalOffsetsMm, concreteSpanMm,
                out var datumAtMinimumProjection, out error))
            return false;
        var stations = normalOffsetsMm
            .Select(offset => datumAtMinimumProjection
                ? concreteMinimum + offset * mm
                : concreteMaximum - offset * mm)
            .OrderBy(station => station)
            .ToList();
        var seedSegments = GeometryKernel.PlanConvexMatLinesAtStations(
            centerlineBoundary, direction, stations);
        if (seedSegments.Count != stations.Count)
        {
            error = "StationSchedule có tim thanh nằm ngoài boundary sau khi áp dụng cover.";
            return false;
        }
        if (seedSegments.Count == 0)
        {
            error = "Không còn station hợp lệ sau cover, khoảng lùi đầu/cuối và spacing.";
            return false;
        }

        var minimumPieceLength = topology.MinimumMatPieceLengthMm * mm;
        var clipped = new List<(XYZ Start, XYZ End, ElementId HostId)>();
        foreach (var seed in seedSegments)
        {
            var seedStart = new XYZ(seed.Start.X, seed.Start.Y, layerWorldZ);
            var seedEnd = new XYZ(seed.End.X, seed.End.Y, layerWorldZ);
            if (!TryClipSegmentToHosts(
                    context, rule, topology, seedStart, seedEnd, barRadius, edgeInset,
                    minimumPieceLength, out var pieces, out var clipError))
            {
                error = clipError;
                return false;
            }
            if (pieces.Count == 0)
            {
                error =
                    $"ABUTMENT_MAT_STATION_OUTSIDE_HOST: {rule.RuleId} có station không giao solid của Rebar host.";
                return false;
            }
            // A station that survives host clipping as more than one run is more than one bar
            // position; the splice planner must not silently lap the fragments of one station.
            if (rule.PiecesPerBarPosition > 1 && pieces.Count > 1)
            {
                error = $"ABUTMENT_MAT_STATION_FRAGMENTED: {rule.RuleId} có station bị host cắt thành " +
                        $"{pieces.Count} đoạn rời; sơ đồ nối chồng chỉ giải được cho station liền một mạch.";
                return false;
            }
            clipped.AddRange(pieces);
        }

        if (clipped.Count == 0)
        {
            error = "Skew-polygon mat produced 0 bars after host/obstacle clipping and minimum-length filtering.";
            return false;
        }

        if (!TryResolveBarPiecePlan(preset, rule, clipped, out var piecePlan, out error)) return false;

        var index = 0;
        foreach (var (start, end, hostId) in clipped)
        {
            index++;
            var alongBar = (end - start).Normalize();
            var positionLengthMm = UnitUtils.ConvertFromInternalUnits(
                (end - start).GetLength(), UnitTypeId.Millimeters);
            if (!AbutmentBarPieceKernel.TryLayOutPosition(
                    piecePlan, index, positionLengthMm, out var layout, out error))
                return false;

            foreach (var piece in layout)
            {
                // The two outer ends stay the measured endpoints themselves. Rebuilding them from a
                // millimeter round trip would move a single-piece bar by a float epsilon, and the
                // layers already live must come out of this wave bit for bit unchanged.
                var pieceStart = piece.PieceIndex == 1
                    ? start
                    : start + alongBar * (piece.StartOffsetMm * mm);
                var pieceEnd = piece.PieceIndex == piece.PieceCount
                    ? end
                    : start + alongBar * (piece.EndOffsetMm * mm);
                bars.Add(new PlannedAbutmentRebar
                {
                    Key = AbutmentBarPieceKernel.FormatPieceKey(
                        rule.RuleId, index, piece.PieceIndex, piece.PieceCount),
                    ZoneCode = rule.ZoneCode,
                    Zone = rule.Zone,
                    HostId = hostId,
                    Rule = rule,
                    ResolvedBarTypeId = type.Id,
                    ResolvedBarTypeName = type.Name,
                    Points = [pieceStart, pieceEnd],
                    PlaneNormal = context.Frame.Vertical,
                    DistributionRepresentation = rule.DistributionRepresentation,
                    DistributionLength = 0,
                    Spacing = spacing,
                    PlannedQuantity = 1,
                    Cover = centerlineOffset - barRadius,
                    CenterlineOffset = centerlineOffset,
                    LayerDepth = layerDepth,
                    PositionIndex = index,
                    PieceIndex = piece.PieceIndex,
                    PieceCount = piece.PieceCount,
                    PieceLengthMm = piece.LengthMm,
                    PositionLengthMm = positionLengthMm,
                    LapLengthMm = piece.LapLengthMm,
                    ReversedLayout = piece.ReversedLayout
                });
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves how many rebar objects one bar position of this rule is built from. A rule that
    /// declares no splice keeps exactly the single-piece shape it always had, down to the bar key,
    /// so the layers already live are untouched. A rule that declares one gets its cut pair solved
    /// once for the whole layer, from the bar length measured on the clipped centerlines — never
    /// from a length typed into the preset, which would freeze one bridge into the pattern.
    /// </summary>
    private static bool TryResolveBarPiecePlan(
        AbutmentRebarPresetV1 preset,
        AbutmentZoneRule rule,
        IReadOnlyList<(XYZ Start, XYZ End, ElementId HostId)> clipped,
        out AbutmentBarPiecePlan plan,
        out string error)
    {
        plan = AbutmentBarPieceKernel.SinglePiecePlan(clipped.Count);
        error = string.Empty;
        if (rule.LapSplice is not { Requirement: AbutmentSpliceRequirement.Required } splice)
            return true;

        if (!preset.TryResolveStandardParameters(rule, out var parameters, out var parameterError))
        {
            error = $"ABUTMENT_BAR_PIECE_STANDARD_PARAMETERS_INCOMPLETE: {rule.RuleId} khai mối nối " +
                    $"nhưng {parameterError}";
            return false;
        }
        if (!preset.TryResolveDeclaredLapSplice(rule, out var lap, out var lapError))
        {
            error = $"ABUTMENT_BAR_PIECE_LAP_LENGTH_UNRESOLVED: {rule.RuleId} không tính lại được nối " +
                    $"chồng từ tiêu chuẩn — {lapError}";
            return false;
        }
        if (!AbutmentBarPieceKernel.TryResolveSplicedFractionLimit(
                splice.SplicedFraction, out var bandLimit, out error))
            return false;

        var lengthsMm = clipped
            .Select(item => UnitUtils.ConvertFromInternalUnits(
                (item.End - item.Start).GetLength(), UnitTypeId.Millimeters))
            .ToList();
        return AbutmentBarPieceKernel.TryPlanSplicedPositions(
            new AbutmentBarPiecePlanRequest
            {
                PositionCount = clipped.Count,
                LongestBarLengthMm = lengthsMm.Max(),
                ShortestBarLengthMm = lengthsMm.Min(),
                LapLengthMm = lap.RequiredMm,
                StockLengthMm = parameters.StockBarLengthMm,
                CuttingModuleMm = splice.CuttingModuleMm,
                SpliceCentreStaggerFraction = splice.SpliceCentreStaggerFraction,
                MaximumSplicedFractionPerSection =
                    Math.Min(bandLimit, rule.Continuity.MaximumSpliceFraction)
            },
            out plan,
            out error);
    }

    /// <summary>
    /// Proves the drawing-shaped description of a layer still expands to the explicit chain the
    /// rule carries, while both sources are kept side by side. The measurement axis and the
    /// projection angle come only from the validated PROFILE parallelogram: the kernel cannot see
    /// a description that names the wrong axis, and taking that axis from the family-instance frame
    /// is the double-skew defect that once stripped 1,6 m off the top transverse mat.
    /// </summary>
    private static bool TryCrossCheckStationLayout(
        AbutmentHostContext context,
        AbutmentZoneRule rule,
        AbutmentTopologyProfile topology,
        AbutmentFootingBoundary boundary,
        PlanPoint2 barDirection,
        IReadOnlyList<double> normalOffsetsMm,
        double concreteSpanMm,
        out string error)
    {
        if (context.ProfileFooting is null)
        {
            error = "ABUTMENT_PROFILE_FOOTING_REQUIRED: stationLayout chỉ đối chiếu được khi biên bệ " +
                    "đã được đo từ các section PROFILE_TM; trục đo không được lấy từ dấu trục family.";
            return false;
        }

        var measuredDirection = rule.StationSchedule.StationLayout!.MeasuredAlong switch
        {
            AbutmentStationReferenceAxis.LongitudinalBoundary => new PlanPoint2(
                boundary.LongitudinalDirection.X, boundary.LongitudinalDirection.Y),
            AbutmentStationReferenceAxis.TransverseBoundary => new PlanPoint2(
                boundary.TransverseDirection.X, boundary.TransverseDirection.Y),
            _ => barDirection
        };
        return AbutmentStationLayoutKernel.TryVerifyDeclaredChain(
            rule.StationSchedule,
            normalOffsetsMm,
            concreteSpanMm,
            barDirection,
            measuredDirection,
            topology.ProfileToSolidMismatchToleranceMm,
            rule.RuleId,
            out _,
            out error);
    }

    /// <summary>
    /// Reports whether station 0 sits on the concrete edge with the smallest projection along
    /// <paramref name="stationNormal"/>. A chain that is not mirror-symmetric silently flips when
    /// the measurement edge changes, and neither the bar count nor the plan-versus-readback check
    /// can see that flip, so an undeclared or unresolvable datum edge must block Create.
    /// </summary>
    private static bool TryAnchorStationDatum(
        AbutmentHostContext context,
        AbutmentZoneRule rule,
        AbutmentTopologyProfile topology,
        bool alongLongitudinal,
        PlanPoint2 stationNormal,
        IReadOnlyList<double> normalOffsetsMm,
        double concreteSpanMm,
        out bool datumAtMinimumProjection,
        out string error)
    {
        datumAtMinimumProjection = true;
        error = string.Empty;
        var symmetryToleranceMm = topology.ProfileToSolidMismatchToleranceMm;
        if (AbutmentStationDatumKernel.IsMirrorSymmetric(
                normalOffsetsMm, concreteSpanMm, symmetryToleranceMm, out var mirrorShiftMm))
            return true;

        if (rule.StationSchedule.DatumEdge == AbutmentStationDatumEdge.Unresolved)
        {
            error =
                $"ABUTMENT_STATION_DATUM_EDGE_REQUIRED: chuỗi station không đối xứng gương " +
                $"(đo từ cạnh bê tông đối diện làm lệch tới {mirrorShiftMm:0.#} mm, " +
                $"vượt {symmetryToleranceMm:0.#} mm), nên stationSchedule.datumEdge phải khai " +
                "ToeSide hoặc HeelSide theo bản vẽ trước khi Create.";
            return false;
        }

        if (!alongLongitudinal)
        {
            error =
                "ABUTMENT_STATION_DATUM_AXIS_UNSUPPORTED: datumEdge ToeSide/HeelSide chỉ neo được " +
                "cho lớp thép dọc, vì mũi/gót bệ nằm theo phương ngang mố.";
            return false;
        }

        var transverse = new PlanPoint2(context.Frame.Transverse.X, context.Frame.Transverse.Y);
        if (transverse.Length <= 1e-9)
        {
            error = "ABUTMENT_STATION_DATUM_AXIS_UNSUPPORTED: trục ngang của local frame không nằm trong mặt bằng.";
            return false;
        }
        var alignment = stationNormal.Dot(transverse.Normalize());
        if (Math.Abs(alignment) < StationDatumAxisAlignmentMinimum)
        {
            error =
                $"ABUTMENT_STATION_DATUM_AXIS_UNSUPPORTED: phương đo station lệch khỏi trục ngang mố " +
                $"(cos={Math.Abs(alignment):0.###} < {StationDatumAxisAlignmentMinimum:0.###}), " +
                "không quy được về cạnh mũi hay cạnh gót.";
            return false;
        }

        if (!AbutmentStemShoulderReader.TryMeasure(context, out var shoulders, out error)) return false;
        if (!AbutmentStationDatumKernel.TryResolveToeSide(
                shoulders,
                topology.ToeWidthMm,
                topology.HeelWidthMm,
                AbutmentStationDatumKernel.ShoulderToleranceMm,
                out var toeSide,
                out _,
                out error))
            return false;

        // The declared edge sits at the minimum or the maximum of the transverse axis; the
        // station normal may still run against that axis, which swaps the two projections.
        var datumAtTransverseMinimum =
            rule.StationSchedule.DatumEdge == AbutmentStationDatumEdge.ToeSide
                ? toeSide == AbutmentStationDatumKernel.DatumSide.AxisMinimum
                : toeSide == AbutmentStationDatumKernel.DatumSide.AxisMaximum;
        datumAtMinimumProjection = datumAtTransverseMinimum == (alignment > 0);
        return true;
    }

    private static bool TryBuildFootingCenterlineBoundary(
        IReadOnlyList<PlanPoint2> polygon,
        AbutmentFootingBoundary boundary,
        AbutmentFootingCoverSpec cover,
        double barRadius,
        out IReadOnlyList<PlanPoint2> centerlineBoundary,
        out double maximumEdgeInset,
        out string error)
    {
        centerlineBoundary = [];
        maximumEdgeInset = 0;
        error = string.Empty;
        var values = new[]
        {
            cover.LongitudinalStartMm,
            cover.LongitudinalEndMm,
            cover.TransverseStartMm,
            cover.TransverseEndMm
        };
        if (values.Any(value => value is not > 0))
        {
            error = "Footing mat thiếu cover cho bốn cạnh.";
            return false;
        }

        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var longitudinalStart = cover.LongitudinalStartMm!.Value * mm + barRadius;
        var longitudinalEnd = cover.LongitudinalEndMm!.Value * mm + barRadius;
        var transverseStart = cover.TransverseStartMm!.Value * mm + barRadius;
        var transverseEnd = cover.TransverseEndMm!.Value * mm + barRadius;
        maximumEdgeInset = new[]
        {
            longitudinalStart, longitudinalEnd, transverseStart, transverseEnd
        }.Max();
        var longitudinal = new PlanPoint2(
            boundary.LongitudinalDirection.X, boundary.LongitudinalDirection.Y).Normalize();
        var transverse = new PlanPoint2(
            boundary.TransverseDirection.X, boundary.TransverseDirection.Y).Normalize();
        centerlineBoundary = GeometryKernel.InsetConvexPolygon(polygon, inwardNormal =>
        {
            var longitudinalAlignment = inwardNormal.Dot(longitudinal);
            var transverseAlignment = inwardNormal.Dot(transverse);
            if (Math.Abs(longitudinalAlignment) >= Math.Abs(transverseAlignment))
                return longitudinalAlignment >= 0 ? longitudinalStart : longitudinalEnd;
            return transverseAlignment >= 0 ? transverseStart : transverseEnd;
        });
        if (centerlineBoundary.Count >= 3) return true;
        error = "Cover theo cạnh làm mất footing centerline boundary.";
        return false;
    }
    private static bool TryBuildProfileClippedSurface(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        AbutmentRebarPresetV1 preset,
        double centerlineOffset,
        double layerDepth,
        RebarTypeInfo type,
        out List<PlannedAbutmentRebar> bars,
        out string error)
    {
        var topology = preset.Topology;
        bars = [];
        error = string.Empty;
        var profile = zone.ProfileGeometry;
        if (profile is null || profile.Kind != zone.Kind)
        {
            error =
                $"ABUTMENT_PROFILE_SURFACE_REQUIRED: zone '{zone.Kind}' requires authoritative L/Center/R profile geometry; solid SurfacePair is not a fallback.";
            return false;
        }

        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var sections = profile.Sections.Select(section =>
            new AbutmentProfileSurfacePathKernel.ProfileSection(
                section.Role,
                section.StationCoordinate,
                section.OrderedPolygonLocal.Select(point =>
                    new AbutmentProfileSurfacePathKernel.ProfilePoint(
                        point.X, point.Y, point.Z)).ToArray())).ToArray();
        var offsets = rule.StationSchedule.CenterlineOffsetsMm
            .Select(offset => offset * mm)
            .ToArray();
        if (!AbutmentProfileSurfacePathKernel.TryBuild(
                zone.Kind,
                rule.GeometryTemplate,
                rule.FaceRole,
                sections,
                offsets,
                centerlineOffset,
                layerDepth,
                topology.ProfilePlanarityToleranceMm * mm,
                topology.ProfileToSolidMismatchToleranceMm * mm,
                out var resolved,
                out error))
            return false;

        var minimumPieceLength = topology.MinimumMatPieceLengthMm * mm;
        var containmentTolerance = topology.ProfileToSolidMismatchToleranceMm * mm;
        var barRadius = type.Diameter / 2;
        var index = 0;
        foreach (var localPath in resolved.Paths)
        {
            var worldPath = localPath.Select(point =>
                context.Frame.ToWorld(
                    point.Longitudinal,
                    point.Transverse,
                    point.Vertical)).ToArray();
            if (!TryClipProfilePathToHost(
                    context,
                    rule,
                    topology,
                    worldPath,
                    barRadius,
                    containmentTolerance,
                    minimumPieceLength,
                    out var clippedPath,
                    out var hostId,
                    out error))
                return false;
            index++;
            bars.Add(new PlannedAbutmentRebar
            {
                Key = $"{rule.RuleId}-{index:D3}",
                ZoneCode = rule.ZoneCode,
                Zone = rule.Zone,
                HostId = hostId,
                Rule = rule,
                ResolvedBarTypeId = type.Id,
                ResolvedBarTypeName = type.Name,
                Points = clippedPath,
                PlaneNormal = resolved.IsVertical
                    ? context.Frame.Longitudinal
                    : context.Frame.Vertical,
                DistributionRepresentation = rule.DistributionRepresentation,
                DistributionLength = 0,
                Spacing = 0,
                PlannedQuantity = 1,
                Cover = centerlineOffset - barRadius,
                CenterlineOffset = centerlineOffset,
                LayerDepth = layerDepth,
                PositionIndex = index
            });
        }

        if (bars.Count == rule.StationSchedule.CenterlineOffsetsMm.Count) return true;
        bars = [];
        error =
            $"ABUTMENT_PROFILE_SURFACE_COUNT_MISMATCH: expected {rule.StationSchedule.CenterlineOffsetsMm.Count} paths, resolved {resolved.Paths.Count}.";
        return false;
    }

    private static bool TryClipProfilePathToHost(
        AbutmentHostContext context,
        AbutmentZoneRule rule,
        AbutmentTopologyProfile topology,
        IReadOnlyList<XYZ> path,
        double barRadius,
        double containmentTolerance,
        double minimumPieceLength,
        out IReadOnlyList<XYZ> clippedPath,
        out ElementId hostId,
        out string error)
    {
        clippedPath = [];
        hostId = ElementId.InvalidElementId;
        error = string.Empty;
        if (path.Count < 2)
        {
            error = "ABUTMENT_PROFILE_SURFACE_PATH_DEGENERATE: profile path has fewer than two points.";
            return false;
        }

        var result = new List<XYZ>(path.Count);
        for (var segmentIndex = 0; segmentIndex + 1 < path.Count; segmentIndex++)
        {
            if (!TryClipSegmentToHosts(
                    context,
                    rule,
                    topology,
                    path[segmentIndex],
                    path[segmentIndex + 1],
                    barRadius,
                    hostBoundaryInset: 0,
                    minimumPieceLength: 0,
                    out var pieces,
                    out error))
                return false;
            if (pieces.Count != 1)
            {
                error =
                    $"ABUTMENT_PROFILE_SURFACE_HOST_AMBIGUOUS: path segment {segmentIndex + 1} intersects {pieces.Count} host pieces.";
                return false;
            }

            var piece = pieces[0];
            if (piece.Start.DistanceTo(path[segmentIndex]) > containmentTolerance ||
                piece.End.DistanceTo(path[segmentIndex + 1]) > containmentTolerance)
            {
                error =
                    $"ABUTMENT_PROFILE_SURFACE_SOLID_MISMATCH: solid clip moves path segment {segmentIndex + 1} beyond the approved profile-to-solid tolerance.";
                return false;
            }
            if (segmentIndex == 0)
            {
                hostId = piece.HostId;
                result.Add(piece.Start);
                result.Add(piece.End);
                continue;
            }
            if (!piece.HostId.Equals(hostId))
            {
                error =
                    "ABUTMENT_PROFILE_SURFACE_SPANS_HOSTS: one profile bar path crosses multiple Rebar hosts.";
                return false;
            }
            var gap = result[^1].DistanceTo(piece.Start);
            if (gap > containmentTolerance)
            {
                error =
                    $"ABUTMENT_PROFILE_SURFACE_CLIP_DISCONTINUITY: adjacent clipped segments are separated by {gap:R}.";
                return false;
            }
            result[^1] = (result[^1] + piece.Start) / 2;
            result.Add(piece.End);
        }

        var totalLength = result.Zip(result.Skip(1),
                (start, end) => start.DistanceTo(end))
            .Sum();
        if (totalLength + 1e-9 < minimumPieceLength)
        {
            error =
                $"ABUTMENT_PROFILE_SURFACE_PIECE_TOO_SHORT: resolved path length {totalLength:R} is below the approved minimum.";
            return false;
        }
        clippedPath = result;
        return true;
    }

    private static bool TryBuildSolidClippedSurface(
        AbutmentHostContext context,
        AbutmentZoneDescriptor zone,
        AbutmentZoneRule rule,
        AbutmentRebarPresetV1 preset,
        double centerlineOffset,
        double layerDepth,
        RebarTypeInfo type,
        out List<PlannedAbutmentRebar> bars,
        out string error)
    {
        var topology = preset.Topology;
        bars = [];
        error = string.Empty;
        if (zone.SurfacePair is null)
        {
            error = $"ABUTMENT_SURFACE_PAIR_UNRESOLVED: zone '{zone.Kind}' không có cặp mặt solid đã đo.";
            return false;
        }

        var face = rule.GeometryTemplate switch
        {
            AbutmentGeometryTemplate.VerticalFront or
                AbutmentGeometryTemplate.HorizontalFront => zone.SurfacePair.FrontFace,
            AbutmentGeometryTemplate.VerticalBack or
                AbutmentGeometryTemplate.HorizontalBack => zone.SurfacePair.BackFace,
            AbutmentGeometryTemplate.VerticalInner or
                AbutmentGeometryTemplate.HorizontalInner => zone.SurfacePair.InnerFace,
            AbutmentGeometryTemplate.VerticalOuter or
                AbutmentGeometryTemplate.HorizontalOuter => zone.SurfacePair.OuterFace,
            _ => null
        };
        if (face is null)
        {
            error = $"ABUTMENT_SURFACE_SEMANTIC_FACE_UNRESOLVED: {rule.RuleId} không có mặt semantic tương ứng.";
            return false;
        }
        var origin = face.Origin;
        var horizontal = face.HorizontalDirection.Normalize();
        var faceVertical = face.Normal.Normalize().CrossProduct(horizontal).Normalize();
        if (faceVertical.DotProduct(context.Frame.Vertical) < 0)
            faceVertical = -faceVertical;
        var loopPolygons = face.BoundaryLoops
            .Select(loop => loop.Select(point =>
                    new PlanPoint2(
                        (point - origin).DotProduct(horizontal),
                        (point - origin).DotProduct(faceVertical)))
                .ToList())
            .Where(loop => loop.Count >= 3)
            .OrderByDescending(loop => Math.Abs(GeometryKernel.SignedArea(loop)))
            .ToList();
        if (loopPolygons.Count == 0)
        {
            error = "ABUTMENT_SURFACE_BOUNDARY_UNRESOLVED: mặt không có outer loop hợp lệ.";
            return false;
        }

        var outer = loopPolygons[0];
        if (outer.Count < 3 || Math.Abs(GeometryKernel.SignedArea(outer)) <= 1e-9)
        {
            error = "ABUTMENT_SURFACE_BOUNDARY_UNRESOLVED: outer loop không hợp lệ.";
            return false;
        }

        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        const double spacing = 0;
        var isVertical = rule.GeometryTemplate is
            AbutmentGeometryTemplate.VerticalFront or
            AbutmentGeometryTemplate.VerticalBack or
            AbutmentGeometryTemplate.VerticalInner or
            AbutmentGeometryTemplate.VerticalOuter;
        var along = isVertical ? new PlanPoint2(0, 1) : new PlanPoint2(1, 0);
        var stationAxis = isVertical ? new PlanPoint2(1, 0) : new PlanPoint2(0, 1);
        if (!GeometryKernel.IsConvexPolygon(outer))
        {
            error = "ABUTMENT_SURFACE_CONCAVE_COVER_UNSUPPORTED: chưa có offset cover bảo toàn polygon lõm.";
            return false;
        }
        var concreteDatum = outer.Min(point => point.Dot(stationAxis));
        var stations = rule.StationSchedule.CenterlineOffsetsMm
            .Select(offset => concreteDatum + offset * mm)
            .ToList();
        var seedSegments = GeometryKernel.PlanPolygonMatLinesAtStations(
            outer, along, stationAxis, centerlineOffset, stations);
        if (seedSegments.Count != stations.Count)
        {
            error = "ABUTMENT_SURFACE_STATION_OUTSIDE_PROFILE: explicit station không tạo đúng một chord trong profile sau cover.";
            return false;
        }
        if (seedSegments.Count == 0)
        {
            error = "Không còn explicit station mặt hợp lệ sau cover.";
            return false;
        }

        var barRadius = type.Diameter / 2;
        var minimumPieceLength = topology.MinimumMatPieceLengthMm * mm;
        var inward = -face.Normal.Normalize() * layerDepth;
        var index = 0;
        foreach (var seed in seedSegments)
        {
            var seedStart = origin + horizontal * seed.Start.X + faceVertical * seed.Start.Y + inward;
            var seedEnd = origin + horizontal * seed.End.X + faceVertical * seed.End.Y + inward;
            if (!TryClipSegmentToHosts(
                    context,
                    rule,
                    topology,
                    seedStart,
                    seedEnd,
                    barRadius,
                    centerlineOffset,
                    minimumPieceLength,
                    out var pieces,
                    out var clipError))
            {
                error = clipError;
                return false;
            }
            if (pieces.Count == 0)
            {
                error =
                    $"ABUTMENT_SURFACE_STATION_OUTSIDE_HOST: {rule.RuleId} có station không giao solid của Rebar host.";
                return false;
            }
            foreach (var (start, end, hostId) in pieces)
            {
                index++;
                bars.Add(new PlannedAbutmentRebar
                {
                    Key = $"{rule.RuleId}-{index:D3}",
                    ZoneCode = rule.ZoneCode,
                    Zone = rule.Zone,
                    HostId = hostId,
                    Rule = rule,
                    ResolvedBarTypeId = type.Id,
                    ResolvedBarTypeName = type.Name,
                    Points = [start, end],
                    PlaneNormal = face.Normal,
                    DistributionRepresentation = rule.DistributionRepresentation,
                    DistributionLength = 0,
                    Spacing = spacing,
                    PlannedQuantity = 1,
                    Cover = centerlineOffset - barRadius,
                    CenterlineOffset = centerlineOffset,
                    LayerDepth = layerDepth,
                    PositionIndex = index
                });
            }
        }

        if (bars.Count == 0)
        {
            error = "Measured surface produced 0 bars after host/obstacle clipping and minimum-length filtering.";
            return false;
        }
        return true;
    }



    private static bool TryClipSegmentToHosts(
        AbutmentHostContext context,
        AbutmentZoneRule rule,
        AbutmentTopologyProfile topology,
        XYZ start,
        XYZ end,
        double barRadius,
        double hostBoundaryInset,
        double minimumPieceLength,
        out IReadOnlyList<(XYZ Start, XYZ End, ElementId HostId)> kept,
        out string error)
    {
        kept = [];
        error = string.Empty;
        if (rule.PileObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail)
        {
            error =
                $"ABUTMENT_RULE_PILE_APPROVED_DETAIL_UNSUPPORTED: {rule.RuleId} chưa có solver né cọc bảo toàn neo, continuity và bù thép.";
            return false;
        }
        if (rule.OpeningObstaclePolicy == AbutmentObstaclePolicy.ApprovedDetail)
        {
            error =
                $"ABUTMENT_RULE_OPENING_APPROVED_DETAIL_UNSUPPORTED: {rule.RuleId} chưa có solver qua opening bảo toàn neo, continuity và bù thép.";
            return false;
        }
        var fullLength = start.DistanceTo(end);
        if (fullLength <= 1e-6)
        {
            error = "ABUTMENT_BAR_ZERO_LENGTH: station tạo đường tâm có chiều dài bằng 0.";
            return false;
        }

        var byHost = new List<(ElementId HostId, IReadOnlyList<(double Start, double End)> Intervals)>();
        foreach (var hostId in context.Solids.Select(item => item.HostId).Distinct())
        {
            if (!TryIntersectSegmentWithHostSolids(
                    context, start, end, hostId, out var intervals, out var intersectionError))
            {
                error = intersectionError;
                return false;
            }
            if (intervals.Count > 0) byHost.Add((hostId, intervals));
        }

        var openingTolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var splitByVoid = byHost.FirstOrDefault(item =>
            item.Intervals.Count > 1 ||
            (context.ComponentHosts.Count <= 1 && item.Intervals.Count == 1 &&
             (item.Intervals[0].Start > openingTolerance ||
              item.Intervals[0].End < fullLength - openingTolerance)));
        if (splitByVoid.Intervals is not null)
        {
            error = rule.OpeningObstaclePolicy switch
            {
                AbutmentObstaclePolicy.Reject =>
                    $"ABUTMENT_OPENING_POLICY_REJECTED: {rule.RuleId} giao void/recess trong host {splitByVoid.HostId}.",
                AbutmentObstaclePolicy.ContinueThroughMonolithicHost =>
                    $"ABUTMENT_OPENING_NOT_MONOLITHIC: {rule.RuleId} bị tách bởi void/recess nên không thể áp dụng ContinueThroughMonolithicHost.",
                _ =>
                    $"ABUTMENT_RULE_OPENING_POLICY_UNRESOLVED: {rule.RuleId} giao void/recess nhưng chưa có policy được duyệt."
            };
            return false;
        }

        var raw = byHost.SelectMany(item =>
            item.Intervals.Select(interval => (item.HostId, interval.Start, interval.End))).ToList();
        for (var first = 0; first < raw.Count; first++)
        for (var second = first + 1; second < raw.Count; second++)
        {
            if (raw[first].HostId == raw[second].HostId) continue;
            var overlap = Math.Min(raw[first].End, raw[second].End) -
                          Math.Max(raw[first].Start, raw[second].Start);
            if (overlap <= 1e-4) continue;
            error = "ABUTMENT_COMPONENT_HOST_OVERLAP: một đoạn thép nằm trong solid của nhiều Rebar host.";
            return false;
        }

        var dir = (end - start).Normalize();
        var separateHosts = context.ComponentHosts.Count > 1;
        var result = new List<(XYZ Start, XYZ End, ElementId HostId)>();
        foreach (var host in byHost)
        foreach (var (h0, h1) in host.Intervals)
        {
            var insetStart = separateHosts ? (h0 > 1e-5 ? h0 + hostBoundaryInset : h0) : h0;
            var insetEnd = separateHosts ? (h1 < fullLength - 1e-5 ? h1 - hostBoundaryInset : h1) : h1;
            if (insetEnd - insetStart <= 1e-6)
            {
                error = "ABUTMENT_COMPONENT_HOST_TOO_SHORT: cover làm mất toàn bộ đoạn thép trong component host.";
                return false;
            }
            var p0 = start + dir * insetStart;
            var p1 = start + dir * insetEnd;
            if (rule.PileObstaclePolicy is AbutmentObstaclePolicy.Reject or AbutmentObstaclePolicy.Unresolved)
            {
                var conflictingPile = FindConflictingPile(context, rule, topology, p0, p1, barRadius);
                if (conflictingPile is not null)
                {
                    if (rule.PileObstaclePolicy == AbutmentObstaclePolicy.Reject)
                    {
                        error = $"ABUTMENT_PILE_POLICY_REJECTED: {rule.RuleId} giao vùng cọc {conflictingPile.SourceId}.";
                        return false;
                    }
                    error = $"ABUTMENT_RULE_PILE_POLICY_UNRESOLVED: {rule.RuleId} giao cọc {conflictingPile.SourceId}.";
                    return false;
                }
            }

            var segmentLength = p0.DistanceTo(p1);
            if (segmentLength + 1e-9 < minimumPieceLength)
            {
                error =
                    $"ABUTMENT_MAT_PIECE_TOO_SHORT: {rule.RuleId} tạo đoạn {UnitUtils.ConvertFromInternalUnits(segmentLength, UnitTypeId.Millimeters):0.#} mm, ngắn hơn minimumMatPieceLengthMm={topology.MinimumMatPieceLengthMm:0.#}.";
                return false;
            }
            // SingleHost: nested solids may carry non-rebar-host element ids;
            // CreateFromCurves must use the primary rebar-capable host.
            var createHostId =
                topology.HostAssemblyStrategy == AbutmentHostAssemblyStrategy.SingleHost ||
                context.ComponentHosts.Count <= 1
                    ? context.Host.Id
                    : host.HostId;
            result.Add((p0, p1, createHostId));
        }
        kept = result;
        return true;
    }

    private static bool TryIntersectSegmentWithHostSolids(
        AbutmentHostContext context,
        XYZ start,
        XYZ end,
        ElementId hostId,
        out IReadOnlyList<(double Start, double End)> merged,
        out string error)
    {
        merged = [];
        error = string.Empty;
        var length = start.DistanceTo(end);
        if (length < 1e-9 || context.Solids.Count == 0) return true;
        var dir = (end - start).Normalize();
        var intervals = new List<(double Start, double End)>();
        try
        {
            var line = Line.CreateBound(start, end);
            using var options = new SolidCurveIntersectionOptions();
            foreach (var solidDesc in context.Solids)
            {
                if (solidDesc.HostId != hostId) continue;
                var solid = solidDesc.Solid;
                if (solid is null || solid.Volume <= 1e-12) continue;
                using var result = solid.IntersectWithCurve(line, options);
                for (var i = 0; i < result.SegmentCount; i++)
                {
                    var segment = result.GetCurveSegment(i);
                    if (segment is null || segment.Length < 1e-4) continue;
                    var a = segment.GetEndPoint(0);
                    var b = segment.GetEndPoint(1);
                    var t0 = (a - start).DotProduct(dir);
                    var t1 = (b - start).DotProduct(dir);
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    t0 = Math.Clamp(t0, 0, length);
                    t1 = Math.Clamp(t1, 0, length);
                    if (t1 - t0 > 1e-4) intervals.Add((t0, t1));
                }
            }
        }
        catch (Exception exception)
        {
            error = $"ABUTMENT_SOLID_INTERSECTION_FAILED: không thể giao đường tâm với host {hostId}: {exception.Message}";
            return false;
        }

        merged = MergeIntervals(intervals);
        return true;
    }

    private static AbutmentPileExclusion? FindConflictingPile(
        AbutmentHostContext context,
        AbutmentZoneRule rule,
        AbutmentTopologyProfile topology,
        XYZ start,
        XYZ end,
        double barRadius)
    {
        var barMinZ = Math.Min(start.Z, end.Z) - barRadius;
        var barMaxZ = Math.Max(start.Z, end.Z) + barRadius;
        foreach (var pile in context.Piles)
        {
            if (!GeometryKernel.OverlapsPileVerticalEnvelope(
                    pile.MinZ, pile.MaxZ, barMinZ, barMaxZ)) continue;
            var placementMargin = rule.PileObstaclePolicy == AbutmentObstaclePolicy.Reject
                ? UnitUtils.ConvertToInternalUnits(topology.PilePlacementMarginMm, UnitTypeId.Millimeters)
                : 0;
            var limit = GeometryKernel.PilePlanningExclusionRadius(
                pile.PileRadius, pile.Clearance, placementMargin, 2 * barRadius);
            var actual = GeometryKernel.DistancePointToSegment(
                new PlanPoint2(pile.Center.X, pile.Center.Y),
                new PlanPoint2(start.X, start.Y),
                new PlanPoint2(end.X, end.Y));
            if (actual < limit - 1e-9) return pile;
        }
        return null;
    }

    private static IReadOnlyList<(double Start, double End)> MergeIntervals(
        IReadOnlyList<(double Start, double End)> intervals)
    {
        if (intervals.Count == 0) return [];
        var ordered = intervals.OrderBy(item => item.Start).ToList();
        var merged = new List<(double Start, double End)> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var last = merged[^1];
            var cur = ordered[i];
            if (cur.Start <= last.End + 1e-4)
                merged[^1] = (last.Start, Math.Max(last.End, cur.End));
            else
                merged.Add(cur);
        }
        return merged;
    }


    /// <summary>
    /// Blocks two footing mats of one face from being planned into the same concrete. Every bar of
    /// a rule carries the single <see cref="PlannedAbutmentRebar.LayerDepth"/> resolved for that
    /// rule, so one bar per rule represents its layer exactly. Layers are grouped by the face they
    /// are covered against instead of by a fixed pair of templates: a face that currently carries
    /// one enabled mat simply has no pair to compare, and a face that grows a third mat is still
    /// compared against both of the others.
    /// </summary>
    private static void ValidateFootingLayerSeparation(AbutmentRebarPlan plan)
    {
        var layers = plan.Bars
            .Where(bar => bar.Zone == AbutmentZoneKind.Footing &&
                          IsFootingMatTemplate(bar.Rule.GeometryTemplate))
            .GroupBy(bar => bar.Rule.RuleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(bar => new AbutmentMatLayerPlacement(
                bar.Rule.RuleId,
                bar.Rule.FaceRole,
                UnitUtils.ConvertFromInternalUnits(bar.LayerDepth, UnitTypeId.Millimeters),
                bar.Rule.DiameterMm))
            .ToList();
        foreach (var overlap in AbutmentMatLayerElevationKernel.FindOverlaps(
                     layers, AbutmentMatLayerElevationKernel.SeparationToleranceMm))
            plan.Issues.Add(new ValidationIssue("ABUTMENT_MAT_LAYER_CLASH",
                ValidationSeverity.Error,
                $"{overlap.FirstRuleId}/{overlap.SecondRuleId} layer separation " +
                $"{overlap.SeparationMm:0.#} mm is smaller than the no-overlap minimum " +
                $"{overlap.RequiredSeparationMm:0.#} mm.",
                plan.Context.Host.Id));
    }

    private static void DetectDuplicates(AbutmentRebarPlan plan)
    {
        var groups = plan.Bars.GroupBy(bar => string.Join("|", bar.Points.Select(point =>
            $"{Math.Round(point.X, 6)},{Math.Round(point.Y, 6)},{Math.Round(point.Z, 6)}")));
        foreach (var duplicate in groups.Where(group => group.Count() > 1))
            plan.Issues.Add(new ValidationIssue("ABUTMENT_DUPLICATE_GEOMETRY", ValidationSeverity.Error,
                $"Rules {string.Join(", ", duplicate.Select(bar => bar.Key))} produce duplicate centerlines."));
    }

    public static string ComputePlanHash(AbutmentRebarPlan plan)
    {
        var text = new StringBuilder()
            .Append(plan.Preset.RuleHash).Append('|')
            .Append(plan.GeometryHash).AppendLine();
        foreach (var bar in plan.Bars.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(bar.Key).Append('|')
                .Append(bar.ZoneCode).Append('|')
                .Append(ElementIdCompat.ToLong(bar.HostId)).Append('|')
                .Append(ElementIdCompat.ToLong(bar.ResolvedBarTypeId)).Append('|')
                .Append(bar.ResolvedBarTypeName).Append('|')
                .Append(bar.DistributionRepresentation).Append('|')
                .Append(bar.PlannedQuantity).Append('|')
                .Append(bar.Spacing.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(bar.LayerDepth.ToString("R", CultureInfo.InvariantCulture));
            foreach (var point in bar.Points)
                text.Append('|')
                    .Append(point.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.Z.ToString("R", CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())))
            .Replace("-", string.Empty);
    }
    private interface IAbutmentRuleGeometryStrategy
    {
        bool TryBuild(
            AbutmentHostContext context,
            AbutmentZoneDescriptor zone,
            AbutmentZoneRule rule,
            AbutmentRebarPresetV1 preset,
            double centerlineOffset,
            double layerDepth,
            RebarTypeInfo type,
            out List<PlannedAbutmentRebar> bars,
            out string error);
    }

    private sealed class ProfileFirstFootingMatGeometryStrategy : IAbutmentRuleGeometryStrategy
    {
        public bool TryBuild(
            AbutmentHostContext context,
            AbutmentZoneDescriptor zone,
            AbutmentZoneRule rule,
            AbutmentRebarPresetV1 preset,
            double centerlineOffset,
            double layerDepth,
            RebarTypeInfo type,
            out List<PlannedAbutmentRebar> bars,
            out string error) =>
            TryBuildProfileFirstMat(
                context, zone, rule, preset, centerlineOffset, layerDepth, type,
                out bars, out error);
    }
    private sealed class ProfileClippedSurfaceGeometryStrategy : IAbutmentRuleGeometryStrategy
    {
        public bool TryBuild(
            AbutmentHostContext context,
            AbutmentZoneDescriptor zone,
            AbutmentZoneRule rule,
            AbutmentRebarPresetV1 preset,
            double centerlineOffset,
            double layerDepth,
            RebarTypeInfo type,
            out List<PlannedAbutmentRebar> bars,
            out string error) =>
            TryBuildProfileClippedSurface(
                context, zone, rule, preset, centerlineOffset, layerDepth, type,
                out bars, out error);
    }
    private sealed class SolidClippedSurfaceGeometryStrategy : IAbutmentRuleGeometryStrategy
    {
        public bool TryBuild(
            AbutmentHostContext context,
            AbutmentZoneDescriptor zone,
            AbutmentZoneRule rule,
            AbutmentRebarPresetV1 preset,
            double centerlineOffset,
            double layerDepth,
            RebarTypeInfo type,
            out List<PlannedAbutmentRebar> bars,
            out string error) =>
            TryBuildSolidClippedSurface(
                context, zone, rule, preset, centerlineOffset, layerDepth, type,
                out bars, out error);
    }

}
#endif

public enum AbutmentBarTypeGradeAssessment
{
    Accepted,
    MetadataMissing,
    BelowRequired
}

/// <summary>
/// Separates geometric RebarBarType readiness from optional material metadata.
/// The evidence-backed rule grade remains authoritative when the Revit type has
/// no Structural Asset; a measured fy below the rule still fails closed.
/// </summary>
public static class AbutmentBarTypeGradeKernel
{
    public static AbutmentBarTypeGradeAssessment Assess(
        double? actualMinimumYieldStrengthMpa,
        double requiredMinimumYieldStrengthMpa,
        double toleranceMpa = 0.5)
    {
        if (actualMinimumYieldStrengthMpa is not { } actual ||
            !double.IsFinite(actual) ||
            actual <= 0)
            return AbutmentBarTypeGradeAssessment.MetadataMissing;
        return actual + toleranceMpa < requiredMinimumYieldStrengthMpa
            ? AbutmentBarTypeGradeAssessment.BelowRequired
            : AbutmentBarTypeGradeAssessment.Accepted;
    }
}

/// <summary>
/// Pure geometry contract for Stem/Backwall bars generated from the authoritative
/// L/Center/R profile sections. Solid faces are intentionally absent from this API:
/// production may use solids only after this kernel has resolved the reviewed path,
/// for host containment and clipping.
/// </summary>
public static class AbutmentProfileSurfacePathKernel
{
    private const double SemanticFaceNormalThreshold = 0.5;

    public enum StrategyKind
    {
        Unsupported,
        ProfileAuthoritative,
        SolidSurface
    }

    public readonly record struct ProfilePoint(
        double Longitudinal,
        double Transverse,
        double Vertical);

    public readonly record struct ProfileSection(
        AbutmentProfileMassKernel.ProfileStationRole Role,
        double Station,
        IReadOnlyList<ProfilePoint> OrderedPolygon);

    public readonly record struct PathPoint(
        double Longitudinal,
        double Transverse,
        double Vertical);

    public readonly record struct Resolution(
        IReadOnlyList<IReadOnlyList<PathPoint>> Paths,
        bool IsVertical,
        AbutmentFaceRole FaceRole,
        double MaximumCenterInterpolationMismatch);

    /// <summary>
    /// Stem and Backwall never fall back to measured solid faces. Wingwalls retain
    /// their existing solid-surface strategy until they have their own profile contract.
    /// </summary>
    public static StrategyKind SelectStrategy(
        AbutmentZoneKind zone,
        AbutmentGeometryTemplate template)
    {
        if (!IsSurfaceTemplate(template)) return StrategyKind.Unsupported;
        return zone switch
        {
            AbutmentZoneKind.Stem or AbutmentZoneKind.Backwall =>
                StrategyKind.ProfileAuthoritative,
            AbutmentZoneKind.WingwallLeft or AbutmentZoneKind.WingwallRight =>
                StrategyKind.SolidSurface,
            _ => StrategyKind.Unsupported
        };
    }

    public static bool TryBuild(
        AbutmentZoneKind zone,
        AbutmentGeometryTemplate template,
        AbutmentFaceRole faceRole,
        IReadOnlyCollection<ProfileSection>? sections,
        IReadOnlyList<double>? stationOffsets,
        double boundaryCenterlineInset,
        double layerDepth,
        double tolerance,
        double interpolationTolerance,
        out Resolution resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;
        if (SelectStrategy(zone, template) != StrategyKind.ProfileAuthoritative)
            return Fail("ABUTMENT_PROFILE_SURFACE_ZONE_UNSUPPORTED",
                $"Zone '{zone}' / template '{template}' does not use the profile surface strategy.",
                out error);
        if (!TryResolveTemplate(template, out var isVertical, out var templateFace))
            return Fail("ABUTMENT_PROFILE_SURFACE_TEMPLATE_UNSUPPORTED",
                $"Template '{template}' is not a supported Front/Back surface path.", out error);
        if (faceRole != templateFace)
            return Fail("ABUTMENT_PROFILE_SURFACE_FACE_ROLE_MISMATCH",
                $"Template '{template}' requires face role '{templateFace}', not '{faceRole}'.", out error);
        if (sections is null || sections.Count != 3)
            return Fail("ABUTMENT_PROFILE_SURFACE_REQUIRED",
                "Stem/Backwall requires exactly three authoritative L/Center/R profile sections.", out error);
        if (stationOffsets is null || stationOffsets.Count == 0 ||
            stationOffsets.Any(value => !double.IsFinite(value) || value < 0) ||
            stationOffsets.Zip(stationOffsets.Skip(1), (first, second) => second - first)
                .Any(delta => delta <= 0))
            return Fail("ABUTMENT_PROFILE_SURFACE_STATIONS_INVALID",
                "Profile surface station offsets must be finite, non-negative and strictly increasing.", out error);
        if (!double.IsFinite(boundaryCenterlineInset) || boundaryCenterlineInset <= 0 ||
            !double.IsFinite(layerDepth) || layerDepth <= 0 ||
            !double.IsFinite(tolerance) || tolerance <= 0 ||
            !double.IsFinite(interpolationTolerance) || interpolationTolerance < 0)
            return Fail("ABUTMENT_PROFILE_SURFACE_TOLERANCE_INVALID",
                "Profile surface cover, layer depth and tolerances must be finite and positive.", out error);

        if (!TryNormalizeSections(
                sections, tolerance, interpolationTolerance,
                out var ordered, out var polygons, out var maximumMismatch, out error))
            return false;

        var facePaths = new List<IReadOnlyList<PlanPoint2>>(3);
        foreach (var polygon in polygons)
        {
            if (!TryInsetFacePath(
                    polygon, templateFace, boundaryCenterlineInset, layerDepth, tolerance,
                    out var facePath, out error))
                return false;
            facePaths.Add(facePath);
        }
        if (facePaths.Select(path => path.Count).Distinct().Count() != 1)
            return Fail("ABUTMENT_PROFILE_SURFACE_TOPOLOGY_AMBIGUOUS",
                "L/Center/R inset semantic face paths do not have matching vertex correspondence.", out error);

        var leftStation = ordered[0].Station;
        var rightStation = ordered[2].Station;
        var coveredStart = leftStation + boundaryCenterlineInset;
        var coveredEnd = rightStation - boundaryCenterlineInset;
        if (coveredEnd - coveredStart <= tolerance)
            return Fail("ABUTMENT_PROFILE_SURFACE_COVER_COLLAPSE",
                "End cover collapses the longitudinal profile span.", out error);

        var paths = new List<IReadOnlyList<PathPoint>>(stationOffsets.Count);
        var concreteVerticalDatum = polygons.SelectMany(polygon => polygon).Min(point => point.Y);
        foreach (var offset in stationOffsets)
        {
            if (isVertical)
            {
                var station = leftStation + offset;
                if (station < coveredStart - tolerance || station > coveredEnd + tolerance)
                    return Fail("ABUTMENT_PROFILE_SURFACE_STATION_OUTSIDE_COVER",
                        $"Longitudinal station {station:R} lies outside the covered profile span.", out error);
                if (!TryInterpolateFacePath(
                        ordered, facePaths, station, tolerance, out var interpolated, out error))
                    return false;
                paths.Add(interpolated
                    .Select(point => new PathPoint(station, point.X, point.Y))
                    .ToArray());
                continue;
            }

            var elevation = concreteVerticalDatum + offset;
            var longitudinalSamples = new List<double> { coveredStart };
            longitudinalSamples.AddRange(ordered
                .Select(section => section.Station)
                .Where(station => station > coveredStart + tolerance &&
                                  station < coveredEnd - tolerance));
            longitudinalSamples.Add(coveredEnd);
            longitudinalSamples = longitudinalSamples
                .OrderBy(value => value)
                .Distinct()
                .ToList();
            var horizontal = new List<PathPoint>(longitudinalSamples.Count);
            foreach (var station in longitudinalSamples)
            {
                if (!TryInterpolateFacePath(
                        ordered, facePaths, station, tolerance, out var interpolated, out error))
                    return false;
                if (!TryPointAtElevation(interpolated, elevation, tolerance, out var transverse))
                    return Fail("ABUTMENT_PROFILE_SURFACE_STATION_OUTSIDE_PROFILE",
                        $"Elevation station {elevation:R} does not intersect exactly one semantic face path.",
                        out error);
                horizontal.Add(new PathPoint(station, transverse, elevation));
            }
            paths.Add(horizontal);
        }

        if (paths.Any(path => path.Count < 2 || path.Zip(path.Skip(1), Distance).Any(length => length <= tolerance)))
            return Fail("ABUTMENT_PROFILE_SURFACE_PATH_DEGENERATE",
                "Profile interpolation produced a zero-length or incomplete bar path.", out error);

        resolution = new Resolution(paths, isVertical, templateFace, maximumMismatch);
        return true;
    }

    private static bool TryNormalizeSections(
        IReadOnlyCollection<ProfileSection> sections,
        double tolerance,
        double interpolationTolerance,
        out ProfileSection[] ordered,
        out IReadOnlyList<PlanPoint2>[] polygons,
        out double maximumMismatch,
        out string error)
    {
        ordered = sections.OrderBy(section => section.Station).ToArray();
        polygons = [];
        maximumMismatch = 0;
        error = string.Empty;
        if (ordered.Any(section => !double.IsFinite(section.Station)) ||
            ordered[1].Station - ordered[0].Station <= tolerance ||
            ordered[2].Station - ordered[1].Station <= tolerance)
            return Fail("ABUTMENT_PROFILE_SURFACE_STATIONS_AMBIGUOUS",
                "Profile section coordinates are non-finite, duplicated or too close.", out error);
        var expectedRoles = new[]
        {
            AbutmentProfileMassKernel.ProfileStationRole.Left,
            AbutmentProfileMassKernel.ProfileStationRole.Center,
            AbutmentProfileMassKernel.ProfileStationRole.Right
        };
        if (!ordered.Select(section => section.Role).SequenceEqual(expectedRoles))
            return Fail("ABUTMENT_PROFILE_SURFACE_ROLES_AMBIGUOUS",
                "Profile sections must be one ordered Left/Center/Right set.", out error);
        if (ordered.Any(section => section.OrderedPolygon is null || section.OrderedPolygon.Count < 3))
            return Fail("ABUTMENT_PROFILE_SURFACE_POLYGON_MISSING",
                "Every profile section must contain one ordered polygon.", out error);
        var vertexCount = ordered[0].OrderedPolygon.Count;
        if (ordered.Any(section => section.OrderedPolygon.Count != vertexCount))
            return Fail("ABUTMENT_PROFILE_SURFACE_TOPOLOGY_AMBIGUOUS",
                "L/Center/R profile polygons do not have matching vertex counts.", out error);

        var projected = new List<IReadOnlyList<PlanPoint2>>(3);
        double? orientation = null;
        foreach (var section in ordered)
        {
            if (section.OrderedPolygon.Any(point =>
                    !double.IsFinite(point.Longitudinal) ||
                    !double.IsFinite(point.Transverse) ||
                    !double.IsFinite(point.Vertical)))
                return Fail("ABUTMENT_PROFILE_SURFACE_NONFINITE",
                    "Profile section contains a non-finite vertex.", out error);
            if (section.OrderedPolygon.Any(point =>
                    Math.Abs(point.Longitudinal - section.Station) > tolerance))
                return Fail("ABUTMENT_PROFILE_SURFACE_NONPLANAR",
                    $"Profile section '{section.Role}' is not planar at its station coordinate.", out error);
            var polygon = section.OrderedPolygon
                .Select(point => new PlanPoint2(point.Transverse, point.Vertical))
                .ToList();
            var area = GeometryKernel.SignedArea(polygon);
            if (Math.Abs(area) <= tolerance * tolerance)
                return Fail("ABUTMENT_PROFILE_SURFACE_POLYGON_DEGENERATE",
                    $"Profile section '{section.Role}' has zero or negligible area.", out error);
            var sign = Math.Sign(area);
            if (orientation is null) orientation = sign;
            else if (Math.Sign(orientation.Value) != sign)
                return Fail("ABUTMENT_PROFILE_SURFACE_TOPOLOGY_AMBIGUOUS",
                    "L/Center/R profile polygons do not use one consistent ordered-loop orientation.", out error);
            if (area < 0) polygon.Reverse();
            projected.Add(polygon);
        }

        var fraction = (ordered[1].Station - ordered[0].Station) /
                       (ordered[2].Station - ordered[0].Station);
        for (var index = 0; index < vertexCount; index++)
        {
            var left = ordered[0].OrderedPolygon[index];
            var center = ordered[1].OrderedPolygon[index];
            var right = ordered[2].OrderedPolygon[index];
            var expected = new ProfilePoint(
                left.Longitudinal + (right.Longitudinal - left.Longitudinal) * fraction,
                left.Transverse + (right.Transverse - left.Transverse) * fraction,
                left.Vertical + (right.Vertical - left.Vertical) * fraction);
            maximumMismatch = Math.Max(maximumMismatch, Distance(center, expected));
        }
        if (maximumMismatch > interpolationTolerance)
            return Fail("ABUTMENT_PROFILE_SURFACE_INTERPOLATION_MISMATCH",
                $"Center profile mismatch {maximumMismatch:R} exceeds {interpolationTolerance:R}.", out error);

        polygons = projected.ToArray();
        return true;
    }

    private static bool TryInsetFacePath(
        IReadOnlyList<PlanPoint2> polygon,
        AbutmentFaceRole faceRole,
        double boundaryInset,
        double layerDepth,
        double tolerance,
        out IReadOnlyList<PlanPoint2> facePath,
        out string error)
    {
        facePath = [];
        error = string.Empty;
        var target = faceRole == AbutmentFaceRole.Front ? 1d : -1d;
        var selected = new bool[polygon.Count];
        var inwardNormals = new PlanPoint2[polygon.Count];
        for (var index = 0; index < polygon.Count; index++)
        {
            var edge = polygon[(index + 1) % polygon.Count] - polygon[index];
            if (edge.Length <= tolerance)
                return Fail("ABUTMENT_PROFILE_SURFACE_POLYGON_DEGENERATE",
                    "Profile region contains a zero-length edge.", out error);
            var inward = new PlanPoint2(-edge.Y, edge.X).Normalize();
            inwardNormals[index] = inward;
            selected[index] = target * inward.X >= SemanticFaceNormalThreshold;
        }
        // A stepped/sloped semantic face may contain short connector edges whose
        // normal is almost vertical. Join such gaps only when none of their edges
        // points toward the opposite semantic face.
        for (var index = 0; index < selected.Length; index++)
        {
            if (!selected[index] || selected[(index + 1) % selected.Length]) continue;
            var gap = new List<int>();
            var cursor = (index + 1) % selected.Length;
            while (!selected[cursor] && gap.Count < selected.Length)
            {
                gap.Add(cursor);
                cursor = (cursor + 1) % selected.Length;
            }
            if (gap.Count == selected.Length ||
                gap.Any(edge => target * inwardNormals[edge].X <= -SemanticFaceNormalThreshold))
                continue;
            foreach (var edge in gap) selected[edge] = true;
        }
        var selectedCount = selected.Count(value => value);
        if (selectedCount == 0 || selectedCount >= polygon.Count)
            return Fail("ABUTMENT_PROFILE_SURFACE_FACE_AMBIGUOUS",
                $"Profile region does not expose one '{faceRole}' semantic edge chain.", out error);
        var chainStarts = Enumerable.Range(0, selected.Length)
            .Where(index => selected[index] && !selected[(index - 1 + selected.Length) % selected.Length])
            .ToArray();
        if (chainStarts.Length != 1)
            return Fail("ABUTMENT_PROFILE_SURFACE_FACE_AMBIGUOUS",
                $"Profile region exposes multiple disjoint '{faceRole}' edge chains.", out error);

        var sourcePath = new List<PlanPoint2>(selectedCount + 1);
        var edgeIndexes = new List<int>(selectedCount);
        var edgeIndex = chainStarts[0];
        sourcePath.Add(polygon[edgeIndex]);
        for (var count = 0; count < selectedCount; count++)
        {
            if (!selected[edgeIndex])
                return Fail("ABUTMENT_PROFILE_SURFACE_FACE_AMBIGUOUS",
                    "Semantic face edge chain is not contiguous.", out error);
            edgeIndexes.Add(edgeIndex);
            sourcePath.Add(polygon[(edgeIndex + 1) % polygon.Count]);
            edgeIndex = (edgeIndex + 1) % polygon.Count;
        }

        var path = new List<PlanPoint2>(sourcePath.Count)
        {
            sourcePath[0] + inwardNormals[edgeIndexes[0]] * layerDepth
        };
        for (var index = 1; index + 1 < sourcePath.Count; index++)
        {
            var previousEdgeIndex = edgeIndexes[index - 1];
            var currentEdgeIndex = edgeIndexes[index];
            var previousStart = sourcePath[index - 1] +
                                inwardNormals[previousEdgeIndex] * layerDepth;
            var currentStart = sourcePath[index] +
                               inwardNormals[currentEdgeIndex] * layerDepth;
            if (!TryLineIntersection(
                    previousStart,
                    sourcePath[index] - sourcePath[index - 1],
                    currentStart,
                    sourcePath[index + 1] - sourcePath[index],
                    out var intersection))
                return Fail("ABUTMENT_PROFILE_SURFACE_OFFSET_AMBIGUOUS",
                    "Adjacent semantic face edges cannot be offset deterministically.", out error);
            path.Add(intersection);
        }
        path.Add(sourcePath[^1] + inwardNormals[edgeIndexes[^1]] * layerDepth);
        if (!TryTrimOpenPath(path, boundaryInset, tolerance, out path))
            return Fail("ABUTMENT_PROFILE_SURFACE_COVER_COLLAPSE",
                "End cover collapses the semantic profile face path.", out error);
        if (path[0].Y > path[^1].Y) path.Reverse();
        if (path.Zip(path.Skip(1), (first, second) => second.Y - first.Y)
            .Any(delta => delta <= tolerance))
            return Fail("ABUTMENT_PROFILE_SURFACE_FACE_AMBIGUOUS",
                $"The '{faceRole}' semantic face path is not strictly monotone in elevation.", out error);
        facePath = path;
        return true;
    }

    private static bool TryTrimOpenPath(
        IReadOnlyList<PlanPoint2> source,
        double trim,
        double tolerance,
        out List<PlanPoint2> result)
    {
        result = source.ToList();
        if (!TryTrimStart(result, trim, tolerance, out result)) return false;
        result.Reverse();
        if (!TryTrimStart(result, trim, tolerance, out result)) return false;
        result.Reverse();
        return result.Count >= 2;

        static bool TryTrimStart(
            IReadOnlyList<PlanPoint2> path,
            double distance,
            double tolerance,
            out List<PlanPoint2> trimmed)
        {
            trimmed = path.ToList();
            var remaining = distance;
            while (trimmed.Count >= 2)
            {
                var segment = trimmed[1] - trimmed[0];
                var length = segment.Length;
                if (length <= tolerance)
                {
                    trimmed.RemoveAt(0);
                    continue;
                }
                if (remaining < length - tolerance)
                {
                    trimmed[0] += segment.Normalize() * remaining;
                    return true;
                }
                remaining -= length;
                trimmed.RemoveAt(0);
            }
            return remaining <= tolerance && trimmed.Count >= 2;
        }
    }

    private static bool TryLineIntersection(
        PlanPoint2 firstOrigin,
        PlanPoint2 firstDirection,
        PlanPoint2 secondOrigin,
        PlanPoint2 secondDirection,
        out PlanPoint2 intersection)
    {
        intersection = default;
        var denominator = Cross(firstDirection, secondDirection);
        if (Math.Abs(denominator) <= 1e-12) return false;
        var parameter = Cross(secondOrigin - firstOrigin, secondDirection) / denominator;
        intersection = firstOrigin + firstDirection * parameter;
        return double.IsFinite(intersection.X) && double.IsFinite(intersection.Y);
    }

    private static double Cross(PlanPoint2 first, PlanPoint2 second) =>
        first.X * second.Y - first.Y * second.X;

    private static bool TryInterpolateFacePath(
        IReadOnlyList<ProfileSection> ordered,
        IReadOnlyList<IReadOnlyList<PlanPoint2>> paths,
        double station,
        double tolerance,
        out IReadOnlyList<PlanPoint2> interpolated,
        out string error)
    {
        interpolated = [];
        error = string.Empty;
        if (station < ordered[0].Station - tolerance || station > ordered[2].Station + tolerance)
            return Fail("ABUTMENT_PROFILE_SURFACE_INTERPOLATION_OUTSIDE",
                "Requested station lies outside the L/R profile span.", out error);
        var first = station <= ordered[1].Station ? 0 : 1;
        var second = first + 1;
        var denominator = ordered[second].Station - ordered[first].Station;
        if (denominator <= tolerance || paths[first].Count != paths[second].Count)
            return Fail("ABUTMENT_PROFILE_SURFACE_INTERPOLATION_AMBIGUOUS",
                "Profile face paths cannot be paired for interpolation.", out error);
        var fraction = Math.Clamp((station - ordered[first].Station) / denominator, 0, 1);
        interpolated = Enumerable.Range(0, paths[first].Count)
            .Select(index => paths[first][index] +
                             (paths[second][index] - paths[first][index]) * fraction)
            .ToArray();
        return true;
    }

    private static bool TryPointAtElevation(
        IReadOnlyList<PlanPoint2> path,
        double elevation,
        double tolerance,
        out double transverse)
    {
        transverse = 0;
        var hits = new List<double>();
        for (var index = 0; index + 1 < path.Count; index++)
        {
            var start = path[index];
            var end = path[index + 1];
            var minimum = Math.Min(start.Y, end.Y);
            var maximum = Math.Max(start.Y, end.Y);
            if (elevation < minimum - tolerance || elevation > maximum + tolerance) continue;
            var delta = end.Y - start.Y;
            if (Math.Abs(delta) <= tolerance) continue;
            var fraction = Math.Clamp((elevation - start.Y) / delta, 0, 1);
            var value = start.X + (end.X - start.X) * fraction;
            if (hits.All(hit => Math.Abs(hit - value) > tolerance)) hits.Add(value);
        }
        if (hits.Count != 1) return false;
        transverse = hits[0];
        return true;
    }

    private static bool TryResolveTemplate(
        AbutmentGeometryTemplate template,
        out bool isVertical,
        out AbutmentFaceRole faceRole)
    {
        isVertical = template is AbutmentGeometryTemplate.VerticalFront or
            AbutmentGeometryTemplate.VerticalBack;
        faceRole = template switch
        {
            AbutmentGeometryTemplate.VerticalFront or AbutmentGeometryTemplate.HorizontalFront =>
                AbutmentFaceRole.Front,
            AbutmentGeometryTemplate.VerticalBack or AbutmentGeometryTemplate.HorizontalBack =>
                AbutmentFaceRole.Back,
            _ => AbutmentFaceRole.Unresolved
        };
        return faceRole != AbutmentFaceRole.Unresolved;
    }

    private static bool IsSurfaceTemplate(AbutmentGeometryTemplate template) =>
        template is AbutmentGeometryTemplate.VerticalFront
            or AbutmentGeometryTemplate.VerticalBack
            or AbutmentGeometryTemplate.HorizontalFront
            or AbutmentGeometryTemplate.HorizontalBack
            or AbutmentGeometryTemplate.VerticalInner
            or AbutmentGeometryTemplate.VerticalOuter
            or AbutmentGeometryTemplate.HorizontalInner
            or AbutmentGeometryTemplate.HorizontalOuter;

    private static double Distance(ProfilePoint first, ProfilePoint second)
    {
        var longitudinal = first.Longitudinal - second.Longitudinal;
        var transverse = first.Transverse - second.Transverse;
        var vertical = first.Vertical - second.Vertical;
        return Math.Sqrt(longitudinal * longitudinal + transverse * transverse + vertical * vertical);
    }

    private static double Distance(PathPoint first, PathPoint second)
    {
        var longitudinal = first.Longitudinal - second.Longitudinal;
        var transverse = first.Transverse - second.Transverse;
        var vertical = first.Vertical - second.Vertical;
        return Math.Sqrt(longitudinal * longitudinal + transverse * transverse + vertical * vertical);
    }

    private static bool Fail(string code, string message, out string error)
    {
        error = $"{code}: {message}";
        return false;
    }
}
