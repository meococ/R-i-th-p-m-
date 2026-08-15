using Autodesk.Revit.DB;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

public sealed class AbutmentClassifier
{
    public AbutmentClassification Classify(AbutmentClassification extraction, AbutmentRebarPresetV1 preset)
    {
        if (extraction.Status != AbutmentResolutionStatus.Resolved || extraction.Context is null)
            return extraction;

        var context = extraction.Context;
        var box = context.OverallBox;
        if (context.FootingFootprint is not { IsValid: true } actualFooting)
        {
            extraction.Status = AbutmentResolutionStatus.Ambiguous;
            extraction.Issues.Add(new ValidationIssue("ABUTMENT_FOOTING_TOP_UNRESOLVED",
                ValidationSeverity.Error,
                "Không có footing profile/top hợp lệ; không thể xác định cao độ lớp thép trên.",
                context.Host.Id));
            return extraction;
        }

        var footingSpec = new AbutmentTopologySpecKernel(
            FootingDepth: actualFooting.Height,
            BackwallHeight: 0,
            WingwallLength: 0,
            StemThickness: 0,
            FootingLength: actualFooting.Length,
            FootingWidth: actualFooting.Depth);
        var kernelBox = new AbutmentBoxKernel(
            box.Min.X, box.Max.X, box.Min.Y, box.Max.Y, box.Min.Z, box.Max.Z);
        List<AbutmentVector> localPoints =
        [
            new(actualFooting.Min.X, actualFooting.Min.Y, actualFooting.Min.Z),
            new(actualFooting.Max.X, actualFooting.Min.Y, actualFooting.Min.Z),
            new(actualFooting.Min.X, actualFooting.Max.Y, actualFooting.Min.Z),
            new(actualFooting.Max.X, actualFooting.Max.Y, actualFooting.Min.Z),
            new(actualFooting.Min.X, actualFooting.Min.Y, actualFooting.Max.Z),
            new(actualFooting.Max.X, actualFooting.Min.Y, actualFooting.Max.Z),
            new(actualFooting.Min.X, actualFooting.Max.Y, actualFooting.Max.Z),
            new(actualFooting.Max.X, actualFooting.Max.Y, actualFooting.Max.Z)
        ];
        // The footing faces carry every cover plane, so they come from the profile-authoritative
        // footprint. context.OverallBox floors on the lowest solid of the whole host.
        var footingVertical = new AbutmentVerticalRangeKernel(
            actualFooting.Min.Z, actualFooting.Max.Z);
        if (!AbutmentKernel.TryResolveFootingBox(
                kernelBox, footingSpec, localPoints, footingVertical,
                out var footing, out var footingError))
        {
            extraction.Status = AbutmentResolutionStatus.Ambiguous;
            extraction.Issues.Add(new ValidationIssue("ABUTMENT_FOOTING_DIMENSIONS_AMBIGUOUS",
                ValidationSeverity.Error,
                $"Không thể nhận diện vùng Footing từ profile-authoritative footprint: {footingError}.",
                context.Host.Id));
            return extraction;
        }

        AddCore(extraction, AbutmentZoneKind.Footing, "GEOMETRY:Footing", footing);
        AddProfileCoreZones(extraction);
        AddMeasuredSurfaceZones(extraction, preset);
        AddProfileEvidenceIssues(extraction, preset);
        AddStemShoulderIssue(extraction, preset);

        var required = preset.Topology.RequiredZones
            .Concat(preset.Rules.Where(rule => rule.Enabled).Select(rule => rule.Zone))
            .Distinct()
            .ToList();
        foreach (var kind in required)
        {
            var count = extraction.Zones.Count(zone => zone.Kind == kind);
            if (count == 1) continue;
            extraction.Issues.Add(new ValidationIssue(
                "ABUTMENT_ZONE_NOT_UNIQUE",
                ValidationSeverity.Error,
                $"Vùng bắt buộc '{kind}' phải được nhận diện duy nhất; hiện có {count}.",
                context.Host.Id));
        }

        var resolvedKinds = extraction.Zones.Select(zone => zone.Kind).OrderBy(kind => kind).ToList();
        extraction.Issues.Add(new ValidationIssue(
            "ABUTMENT_CANONICAL_ZONES_RESOLVED",
            ValidationSeverity.Info,
            $"Đã nhận diện canonical zone: {string.Join(", ", resolvedKinds)}. " +
            "PROFILE_TM là section/outline authority; solid host dùng containment và mismatch validation. " +
            "A1 chỉ dùng Footing; zone ngoài phạm vi không chặn Create.",
            context.Host.Id));

        extraction.Status = extraction.Issues.Any(issue => issue.Severity == ValidationSeverity.Error)
            ? AbutmentResolutionStatus.Ambiguous
            : AbutmentResolutionStatus.Resolved;
        return extraction;
    }

    private static void AddMeasuredSurfaceZones(
        AbutmentClassification result,
        AbutmentRebarPresetV1 preset)
    {
        var topology = preset.Topology;
        var context = result.Context!;
        var footing = context.FootingFootprint!;
        var dimensions = context.Dimensions;
        var faces = context.PlanarFaces.Select((face, index) =>
        {
            var origin = context.Frame.ToLocal(face.Origin);
            return new AbutmentPlanarFaceKernel(
                index,
                face.LocalBox.Min.X, face.LocalBox.Max.X,
                face.LocalBox.Min.Y, face.LocalBox.Max.Y,
                face.LocalBox.Min.Z, face.LocalBox.Max.Z,
                face.Normal.DotProduct(context.Frame.Longitudinal),
                face.Normal.DotProduct(context.Frame.Transverse),
                face.Normal.DotProduct(context.Frame.Vertical),
                origin.X, origin.Y, origin.Z,
                face.Area);
        }).ToList();
        if (faces.Count == 0) return;

        var minimumStemHeightMm = new[]
            {
                dimensions.StemHeightLeftMm,
                dimensions.StemHeightRightMm
            }
            .Where(value => value > 0)
            .DefaultIfEmpty(2500)
            .Min();
        var minimumStemHeight = Mm(minimumStemHeightMm);
        var stemThickness = Mm(dimensions.StemBaseThicknessMm);
        var backwallThickness = Mm(dimensions.StemTopThicknessMm);
        const double thicknessToleranceMm = 150;

        // Stem/Backwall geometry is profile-authoritative. Solid surface pairs remain
        // a legacy review fallback only when no validated core profile is available.
        if (context.CoreProfile is null && !topology.RequireProfileMassMarkers && stemThickness > 0 &&
            AbutmentSurfaceZoneKernel.TryResolveStemPair(
                faces,
                footing.Max.Z,
                footing.Length,
                minimumStemHeight * 0.70,
                stemThickness,
                Mm(50),
                Mm(thicknessToleranceMm),
                out var stemPair))
        {
            AddSurfaceZone(result, AbutmentZoneKind.Stem, stemPair, wingSide: 0);
        }

        if (context.CoreProfile is null && !topology.RequireProfileMassMarkers && backwallThickness > 0 &&
            AbutmentSurfaceZoneKernel.TryResolveBackwallPair(
                faces,
                footing.Max.Z,
                footing.Length,
                minimumStemHeight * 0.75,
                Math.Max(Mm(300), minimumStemHeight * 0.12),
                backwallThickness,
                Mm(thicknessToleranceMm),
                out var backwallPair))
        {
            AddSurfaceZone(result, AbutmentZoneKind.Backwall, backwallPair, wingSide: 0);
        }

        var wingLengthMm = dimensions.WingLengthMm > 0
            ? dimensions.WingLengthMm
            : topology.WingwallLengthMm;
        var wingLengthLeftMm = AbutmentWingProfileKernel.ResolveExpectedLength(
            -1, dimensions.WingLengthLeftMm, dimensions.WingLengthRightMm, wingLengthMm);
        var wingLengthRightMm = AbutmentWingProfileKernel.ResolveExpectedLength(
            1, dimensions.WingLengthLeftMm, dimensions.WingLengthRightMm, wingLengthMm);
        var wingThicknessMm = dimensions.WingThicknessMm > 0
            ? dimensions.WingThicknessMm
            : topology.WingwallThicknessMm;
        var wingLengthLeft = Mm(wingLengthLeftMm);
        var wingLengthRight = Mm(wingLengthRightMm);
        var wingThickness = Mm(wingThicknessMm);
        if (wingLengthLeft <= 0 || wingLengthRight <= 0 || wingThickness <= 0) return;
        var wingPairs = AbutmentSurfaceZoneKernel.ResolveWingPairs(
            faces,
            footing.Center.X,
            wingLengthLeft * 0.50,
            wingLengthRight * 0.50,
            minimumStemHeight * 0.50,
            wingThickness,
            Mm(thicknessToleranceMm));
        foreach (var pair in wingPairs)
        {
            var first = context.PlanarFaces[pair.FrontFaceIndex];
            var second = context.PlanarFaces[pair.BackFaceIndex];
            var center = (first.LocalBox.Center.X + second.LocalBox.Center.X) / 2;
            var side = center < footing.Center.X ? -1 : 1;
            var kind = side < 0 ? AbutmentZoneKind.WingwallLeft : AbutmentZoneKind.WingwallRight;
            var zone = AddSurfaceZone(
                result,
                kind,
                pair,
                side,
                faces,
                footing.Center.X,
                footing.Center.Y);
            if (zone?.SurfacePair?.InnerFace is null)
            {
                var enabled = preset.Rules.Any(rule => rule.Enabled && rule.Zone == kind);
                result.Issues.Add(new ValidationIssue(
                    "ABUTMENT_WING_INNER_OUTER_UNRESOLVED",
                    enabled ? ValidationSeverity.Error : ValidationSeverity.Warning,
                    $"Không xác định duy nhất InnerFace/OuterFace cho {kind}; Create cánh phải fail-closed.",
                    context.Host.Id));
                continue;
            }
            AddWingProfileChecksumIssue(result, preset, zone, side,
                side < 0 ? wingLengthLeftMm : wingLengthRightMm,
                AbutmentDeclaredDimensionKernel.ResolveSource(
                    dimensions.WingLengthMm, topology.WingwallLengthMm));
        }
    }

    private static void AddProfileCoreZones(AbutmentClassification result)
    {
        var context = result.Context!;
        if (context.CoreProfile is not { Sections.Count: 3 } core) return;

        AddProfileZone(AbutmentZoneKind.Stem, section => section.StemPolygonLocal);
        AddProfileZone(AbutmentZoneKind.Backwall, section => section.BackwallPolygonLocal);
        result.Issues.Add(new ValidationIssue(
            "ABUTMENT_PROFILE_CORE_ZONES_RESOLVED",
            ValidationSeverity.Info,
            "Đã nhận diện Stem và Backwall từ ba section PROFILE_TM; solid chỉ map host/containment, không làm đường biên authority.",
            context.Host.Id));
        return;

        void AddProfileZone(
            AbutmentZoneKind kind,
            Func<AbutmentCoreProfileSectionEvidence, IReadOnlyList<XYZ>> selectPolygon)
        {
            var profileSections = core.Sections
                .OrderBy(section => section.StationCoordinate)
                .Select(section => new AbutmentProfileZoneSection
                {
                    Role = section.Role,
                    StationCoordinate = section.StationCoordinate,
                    OrderedPolygonLocal = selectPolygon(section)
                })
                .ToArray();
            var points = profileSections.SelectMany(section => section.OrderedPolygonLocal).ToArray();
            if (points.Length == 0) return;
            var zone = new AbutmentZoneDescriptor
            {
                Kind = kind,
                Code = $"GEOMETRY:{kind}",
                LocalBox = new AbutmentLocalBox
                {
                    Min = new XYZ(
                        points.Min(point => point.X),
                        points.Min(point => point.Y),
                        points.Min(point => point.Z)),
                    Max = new XYZ(
                        points.Max(point => point.X),
                        points.Max(point => point.Y),
                        points.Max(point => point.Z))
                },
                ProfileGeometry = new AbutmentProfileZoneGeometry
                {
                    Kind = kind,
                    Sections = profileSections,
                    GeometryHash = core.GeometryHash + ":" + kind
                }
            };
            if (!zone.LocalBox.IsValid) return;
            for (var index = 0; index < context.Solids.Count; index++)
            {
                if (Intersects(zone.LocalBox, context.Solids[index].LocalBox))
                    zone.SupportingSolidIndexes.Add(index);
            }
            result.Zones.Add(zone);
        }
    }

    private static AbutmentZoneDescriptor? AddSurfaceZone(
        AbutmentClassification result,
        AbutmentZoneKind kind,
        AbutmentSurfacePairKernel pair,
        int wingSide,
        IReadOnlyList<AbutmentPlanarFaceKernel>? faceKernels = null,
        double coreLongitudinal = 0,
        double coreTransverse = 0)
    {
        var context = result.Context!;
        var first = context.PlanarFaces[pair.FrontFaceIndex];
        var second = context.PlanarFaces[pair.BackFaceIndex];
        AbutmentPlanarFaceDescriptor front;
        AbutmentPlanarFaceDescriptor back;
        if (wingSide == 0)
        {
            front = first;
            back = second;
        }
        else
        {
            var firstNormal = first.Normal.DotProduct(context.Frame.Longitudinal) * wingSide;
            var secondNormal = second.Normal.DotProduct(context.Frame.Longitudinal) * wingSide;
            front = firstNormal >= secondNormal ? first : second;
            back = ReferenceEquals(front, first) ? second : first;
        }

        AbutmentPlanarFaceDescriptor? inner = null;
        AbutmentPlanarFaceDescriptor? outer = null;
        if (wingSide != 0 && faceKernels is not null &&
            AbutmentWingProfileKernel.TryResolveInnerOuter(
                faceKernels, pair, coreLongitudinal, coreTransverse, out var semantics))
        {
            inner = context.PlanarFaces[semantics.InnerFaceIndex];
            outer = context.PlanarFaces[semantics.OuterFaceIndex];
        }

        var zone = new AbutmentZoneDescriptor
        {
            Kind = kind,
            Code = $"GEOMETRY:{kind}",
            LocalBox = Union(front.LocalBox, back.LocalBox),
            SurfacePair = new AbutmentZoneSurfacePair
            {
                FrontFace = front,
                BackFace = back,
                InnerFace = inner,
                OuterFace = outer
            }
        };
        if (!zone.LocalBox.IsValid) return null;
        zone.SupportingSolidIndexes.Add(front.SolidIndex);
        if (back.SolidIndex != front.SolidIndex)
            zone.SupportingSolidIndexes.Add(back.SolidIndex);
        result.Zones.Add(zone);
        return zone;
    }

    private static void AddWingProfileChecksumIssue(
        AbutmentClassification result,
        AbutmentRebarPresetV1 preset,
        AbutmentZoneDescriptor zone,
        int side,
        double expectedLengthMm,
        AbutmentDeclaredValueSource expectedLengthSource)
    {
        var context = result.Context!;
        var inner = zone.SurfacePair!.InnerFace!;
        var projections = inner.BoundaryLoops
            .SelectMany(loop => loop)
            .Select(point => (point - inner.Origin).DotProduct(inner.HorizontalDirection))
            .ToList();
        if (projections.Count == 0) return;
        var observedLengthMm = UnitUtils.ConvertFromInternalUnits(
            projections.Max() - projections.Min(), UnitTypeId.Millimeters);
        var check = AbutmentDeclaredDimensionKernel.Compare(
            $"{zone.Kind} chiều dài tường cánh", observedLengthMm, expectedLengthMm, expectedLengthSource);
        var enabled = preset.Rules.Any(rule => rule.Enabled && rule.Zone == zone.Kind);
        var dimensions = context.Dimensions;
        var innerDatumMm = side < 0
            ? dimensions.WingInnerLengthLeftMm
            : dimensions.WingInnerLengthRightMm;
        var outerDatumMm = side < 0
            ? dimensions.WingOuterLengthLeftMm
            : dimensions.WingOuterLengthRightMm;
        result.Issues.Add(new ValidationIssue(
            check.Matches ? "ABUTMENT_WING_PROFILE_CHECKSUM_MATCH" : "ABUTMENT_WING_PROFILE_CHECKSUM_MISMATCH",
            check.Blocks && enabled ? ValidationSeverity.Error
                : check.Matches ? ValidationSeverity.Info
                : ValidationSeverity.Warning,
            AbutmentDeclaredDimensionKernel.Describe(check) +
            $" Mốc trong={innerDatumMm:0.#} mm, mốc ngoài={outerDatumMm:0.#} mm. " +
            "BoundaryLoops là geometry authority; số khai chỉ là checksum. " +
            (enabled
                ? "Rule của zone đang bật; chỉ family tự mâu thuẫn mới chặn Create."
                : "Zone này ngoài phạm vi rule đang bật."),
            context.Host.Id));
    }

    /// <summary>
    /// Reports the measured footing cantilevers against <c>toeWidthMm</c>/<c>heelWidthMm</c>. Those
    /// two numbers only label the two measured shoulders, so a deviation is news about the model,
    /// not a reason to stop: the planner anchors the station chain on the measured concrete edge.
    /// </summary>
    private static void AddStemShoulderIssue(
        AbutmentClassification extraction,
        AbutmentRebarPresetV1 preset)
    {
        var context = extraction.Context!;
        var topology = preset.Topology;
        if (topology.ToeWidthMm <= 0 || topology.HeelWidthMm <= 0) return;
        if (!AbutmentStemShoulderReader.TryMeasure(context, out var shoulders, out _)) return;
        if (!AbutmentStationDatumKernel.TryResolveToeSide(
                shoulders,
                topology.ToeWidthMm,
                topology.HeelWidthMm,
                AbutmentStationDatumKernel.ShoulderToleranceMm,
                out _,
                out var deviation,
                out var labelError))
        {
            // Only rules that actually anchor a chain on the toe/heel edge need this, and each of
            // them blocks itself in the planner. Recognition records what was measured.
            extraction.Issues.Add(new ValidationIssue(
                "ABUTMENT_STATION_DATUM_SHOULDER_UNRESOLVED",
                ValidationSeverity.Warning,
                labelError,
                context.Host.Id));
            return;
        }
        if (deviation <= AbutmentStationDatumKernel.ShoulderToleranceMm) return;
        extraction.Issues.Add(new ValidationIssue(
            "ABUTMENT_STATION_DATUM_SHOULDER_MISMATCH",
            ValidationSeverity.Warning,
            AbutmentStationDatumKernel.DescribeShoulderDeviation(
                deviation, topology.ToeWidthMm, topology.HeelWidthMm,
                AbutmentStationDatumKernel.ShoulderToleranceMm),
            context.Host.Id));
    }

    private static void AddProfileEvidenceIssues(
        AbutmentClassification extraction,
        AbutmentRebarPresetV1 preset)
    {
        var context = extraction.Context!;
        if (context.ProfileMasses.Count == 0 &&
            (preset.Topology.ProfileMassFamilyNameContains.Count > 0 ||
             preset.Topology.ProfileMassTypeNameContains.Count > 0))
        {
            extraction.Issues.Add(new ValidationIssue(
                preset.Topology.RequireProfileMassMarkers
                    ? "ABUTMENT_PROFILE_MASS_MISSING"
                    : "ABUTMENT_PROFILE_MASS_OPTIONAL_MISSING",
                preset.Topology.RequireProfileMassMarkers
                    ? ValidationSeverity.Error
                    : ValidationSeverity.Warning,
                preset.Topology.RequireProfileMassMarkers
                    ? "Thiếu ba nested PROFILE_TM station sections L/Center/R; Create fail-closed."
                    : "Không đọc được nested PROFILE_TM; đang dùng solid fallback chỉ cho review.",
                context.Host.Id));
        }
        else if (context.ProfileMasses.Count > 0)
        {
            extraction.Issues.Add(new ValidationIssue(
                "ABUTMENT_PROFILE_MASS_OBSERVED",
                ValidationSeverity.Info,
                $"Đã đọc {context.ProfileMasses.Count} PROFILE_TM ordered loop(s); " +
                "không dùng type name, mirror hoặc bounding box làm geometry authority.",
                context.Host.Id));
        }
    }

    private static void Add(
        AbutmentClassification result,
        AbutmentZoneKind kind,
        string code,
        double minX,
        double maxX,
        double minY,
        double maxY,
        double minZ,
        double maxZ)
    {
        var zone = new AbutmentZoneDescriptor
        {
            Kind = kind,
            Code = code,
            LocalBox = new AbutmentLocalBox
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            }
        };
        if (!zone.LocalBox.IsValid) return;
        for (var index = 0; index < result.Context!.Solids.Count; index++)
        {
            if (Intersects(zone.LocalBox, result.Context.Solids[index].LocalBox))
                zone.SupportingSolidIndexes.Add(index);
        }
        result.Zones.Add(zone);
    }

    private static void AddCore(
        AbutmentClassification result,
        AbutmentZoneKind kind,
        string code,
        AbutmentBoxKernel box) =>
        Add(result, kind, code,
            box.MinLongitudinal, box.MaxLongitudinal,
            box.MinTransverse, box.MaxTransverse,
            box.MinVertical, box.MaxVertical);

    private static AbutmentLocalBox Union(AbutmentLocalBox first, AbutmentLocalBox second) => new()
    {
        Min = new XYZ(
            Math.Min(first.Min.X, second.Min.X),
            Math.Min(first.Min.Y, second.Min.Y),
            Math.Min(first.Min.Z, second.Min.Z)),
        Max = new XYZ(
            Math.Max(first.Max.X, second.Max.X),
            Math.Max(first.Max.Y, second.Max.Y),
            Math.Max(first.Max.Z, second.Max.Z))
    };

    private static bool Intersects(AbutmentLocalBox first, AbutmentLocalBox second) =>
        first.Min.X < second.Max.X && first.Max.X > second.Min.X &&
        first.Min.Y < second.Max.Y && first.Max.Y > second.Min.Y &&
        first.Min.Z < second.Max.Z && first.Max.Z > second.Min.Z;

    private static double Mm(double value) =>
        UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
}
