using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using BIM.DatViet.Planning;
using BIM.DatViet.Recognition;
using BIM.DatViet.RevitServices;

namespace BIM.DatViet.Controllers;

public sealed class AbutmentRebarController
{
    private readonly UIDocument _uiDocument;
    private readonly Document _document;
    private readonly Element _host;
    private readonly Stack<AbutmentRebarPresetV1> _undo = new();
    private RebarTypeCatalog _barTypeCatalog;
    private AbutmentRebarPresetV1 _basePreset;
    private AbutmentZoneDescriptor? _boundZone;
    private string _boundZoneHostUniqueId = string.Empty;
    private string _boundZoneGeometryHash = string.Empty;
    private readonly Dictionary<AbutmentZoneKind, AbutmentRebarPlan> _managedPreviewCache = new();

    public AbutmentRebarController(UIDocument uiDocument, Element host)
    {
        _uiDocument = uiDocument;
        _document = uiDocument.Document;
        _host = host;
        var resolvedProfile = new AbutmentProfileResolver(_document).Resolve(host);
        AbutmentDiagnostics.LogMessage(
            "ABUTMENT_PROFILE_RESOLVED",
            $"id={resolvedProfile.ProfileId}; schema={resolvedProfile.SchemaVersion}; " +
            $"ruleVersion={resolvedProfile.RuleVersion}; ruleHash={resolvedProfile.RuleHash}; " +
            $"catalogPriority={resolvedProfile.CatalogPriority}; " +
            $"catalogTimestampUtc={resolvedProfile.CatalogTimestampUtc:O}");
        _basePreset = resolvedProfile.Clone();
        Preset = _basePreset.Clone();
        _barTypeCatalog = new RebarTypeCatalog(_document);
        AlignPresetBarTypes(Preset);
        AlignPresetBarTypes(_basePreset);
    }

    public AbutmentRebarPresetV1 Preset { get; private set; } = null!;
    public AbutmentClassification? Classification { get; private set; }
    public AbutmentRebarPlan? CurrentPlan { get; private set; }
    public ReviewedAbutmentPlanSnapshot? ReviewedSnapshot { get; private set; }
    public IReadOnlyList<string> AvailableBarTypeNames => _barTypeCatalog.Names;
    public bool HasAnyRebarBarType => !_barTypeCatalog.IsEmpty;
    public string RebarBarTypeCatalogSummary =>
        _barTypeCatalog.IsEmpty
            ? "Model chưa có RebarBarType."
            : string.Join(", ", AvailableBarTypeNames.Take(16)) +
              (AvailableBarTypeNames.Count > 16 ? ", ..." : string.Empty);

    public bool CanUndo => _undo.Count > 0;
    public double GetBarTypeDiameterMm(string name)
    {
        if (_barTypeCatalog.TryGet(name, out var info) ||
            _barTypeCatalog.TryResolve(name, expectedDiameterMm: 0, out info, out _))
            return UnitUtils.ConvertFromInternalUnits(info.Diameter, UnitTypeId.Millimeters);
        throw new InvalidOperationException($"Rebar type '{name}' không tồn tại trong model.");
    }
    public Element Host => _host;

    /// <summary>
    /// The tool window is modeless, so Revit can close the document or delete the host while it is
    /// still open. Every entry point that reads the model has to ask first: dereferencing a dead
    /// Element throws deep inside the Revit API, and a cached plan that outlives its host shows the
    /// engineer numbers nothing stands behind.
    /// </summary>
    public bool IsHostLive => _document.IsValidObject && _host.IsValidObject;

    private void EnsureHostLive()
    {
        if (IsHostLive) return;
        throw new InvalidOperationException(
            "Document hoặc host mố đã bị đóng/xóa sau khi mở công cụ; mọi số liệu đang hiển thị " +
            "không còn bằng chứng nào đứng sau. Đóng cửa sổ này và mở lại công cụ trên host mới.");
    }

    public bool HasManagedRebars(AbutmentZoneKind zone) =>
        IsHostLive && _managedPreviewCache.TryGetValue(zone, out var plan) && plan.Bars.Count > 0;

    public AbutmentRebarPlan? BuildManagedReadbackPreview(AbutmentZoneKind zone) =>
        IsHostLive ? _managedPreviewCache.GetValueOrDefault(zone) : null;

    private AbutmentRebarPlan? BuildManagedReadbackPreviewFromDocument(AbutmentZoneKind zone)
    {
        if (Classification?.Context is not { } context) return null;
        var plan = new AbutmentRebarPlan
        {
            Context = context,
            Preset = Preset,
            GeometryHash = context.GeometryHash,
            PlanHash = "MANAGED-READBACK"
        };
        plan.Zones.AddRange(Classification.Zones);

        foreach (var rebar in AbutmentMetadataStore.FindOwned(_document, _host.UniqueId))
        {
            if (!AbutmentMetadataStore.TryRead(rebar, out var metadata) ||
                !metadata.HostRole.Equals(zone.ToString(), StringComparison.OrdinalIgnoreCase))
                continue;
            var rule = ResolveManagedPreviewRule(metadata);
            if (rule is null) continue;

            var positionCount = Math.Max(1, rebar.NumberOfBarPositions);
            for (var position = 0; position < positionCount; position++)
            {
                IList<Curve> curves;
                try
                {
                    curves = rebar.GetCenterlineCurves(
                        false,
                        false,
                        false,
                        MultiplanarOption.IncludeOnlyPlanarCurves,
                        position);
                }
                catch (Exception)
                {
                    continue;
                }

                var points = new List<XYZ>();
                foreach (var curve in curves)
                {
                    foreach (var point in curve.Tessellate())
                    {
                        if (points.Count == 0 || points[^1].DistanceTo(point) > 1e-9)
                            points.Add(point);
                    }
                }
                if (points.Count < 2) continue;

                plan.Bars.Add(new PlannedAbutmentRebar
                {
                    Key = $"managed-{rebar.Id}-{position}",
                    ZoneCode = metadata.RebarZone,
                    Zone = zone,
                    HostId = rebar.GetHostId(),
                    Rule = rule,
                    ResolvedBarTypeId = rebar.GetTypeId(),
                    ResolvedBarTypeName = _document.GetElement(rebar.GetTypeId())?.Name ?? string.Empty,
                    Points = points,
                    PlaneNormal = context.Frame.Vertical,
                    DistributionRepresentation = AbutmentDistributionRepresentation.ExplicitStations,
                    PlannedQuantity = 1
                });
            }
        }

        if (plan.Bars.Count > 0)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var minZ = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var maxZ = double.MinValue;
            foreach (var point in plan.Bars.SelectMany(bar => bar.Points))
            {
                var local = context.Frame.ToLocal(point);
                minX = Math.Min(minX, local.X);
                minY = Math.Min(minY, local.Y);
                minZ = Math.Min(minZ, local.Z);
                maxX = Math.Max(maxX, local.X);
                maxY = Math.Max(maxY, local.Y);
                maxZ = Math.Max(maxZ, local.Z);
            }
            AbutmentDiagnostics.LogMessage(
                "ABUTMENT_MANAGED_PREVIEW",
                $"zone={zone}; bars={plan.Bars.Count}; pointBox=" +
                $"[{minX:R},{minY:R},{minZ:R}]..[{maxX:R},{maxY:R},{maxZ:R}]; " +
                $"hostBox=[{context.OverallBox.Min.X:R},{context.OverallBox.Min.Y:R},{context.OverallBox.Min.Z:R}].." +
                $"[{context.OverallBox.Max.X:R},{context.OverallBox.Max.Y:R},{context.OverallBox.Max.Z:R}]");
        }

        return plan.Bars.Count == 0 ? null : plan;
    }

    private AbutmentZoneRule? ResolveManagedPreviewRule(AbutmentOwnership metadata)
    {
        var current = Preset.Rules.FirstOrDefault(candidate =>
            candidate.RuleId.Equals(metadata.RuleId, StringComparison.OrdinalIgnoreCase) ||
            metadata.RuleId.StartsWith(
                $"{candidate.RuleId}-",
                StringComparison.OrdinalIgnoreCase));
        if (current is not null) return current;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(metadata.SourceEvidenceJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("geometryTemplate", out var templateElement))
                root.TryGetProperty("GeometryTemplate", out templateElement);
            if (templateElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                Enum.TryParse<AbutmentGeometryTemplate>(
                    templateElement.GetString(),
                    ignoreCase: true,
                    out var template))
            {
                var diameterMm = 0d;
                if (!root.TryGetProperty("diameterMm", out var diameterElement))
                    root.TryGetProperty("DiameterMm", out diameterElement);
                if (diameterElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                    diameterElement.TryGetDouble(out diameterMm);
                var evidenceMatch = Preset.Rules.FirstOrDefault(candidate =>
                    candidate.GeometryTemplate == template &&
                    (diameterMm <= 0 || Math.Abs(candidate.DiameterMm - diameterMm) <= 0.1));
                if (evidenceMatch is not null) return evidenceMatch;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Legacy metadata may contain incomplete evidence JSON; fall back to the source mark.
        }

        return Preset.Rules.FirstOrDefault(candidate =>
            metadata.RuleId.StartsWith(
                $"CVC-{candidate.SourceDrawingMark}-",
                StringComparison.OrdinalIgnoreCase));
    }

    public void InvalidateReviewedSnapshot() => ReviewedSnapshot = null;

    public void ClearBoundZone()
    {
        _boundZone = null;
        _boundZoneHostUniqueId = string.Empty;
        _boundZoneGeometryHash = string.Empty;
        ReviewedSnapshot = null;
    }

    public bool TryBindZoneEvidence(
        AbutmentZoneDescriptor zone,
        string hostUniqueId,
        string geometryHash,
        out string error)
    {
        if (!TryValidateSelection(
                zone,
                hostUniqueId,
                geometryHash,
                Classification,
                out _,
                out error))
        {
            ClearBoundZone();
            return false;
        }

        _boundZone = CloneZone(zone);
        _boundZoneHostUniqueId = hostUniqueId;
        _boundZoneGeometryHash = geometryHash;
        ReviewedSnapshot = null;
        return true;
    }

    public bool HasBoundZone(AbutmentZoneKind zone) =>
        TryValidateCurrentSelection(Classification, out var evidence, out _) &&
        evidence.Kind == zone;

    public AbutmentZoneDescriptor? CurrentBoundZone =>
        _boundZone is null ? null : CloneZone(_boundZone);

    public AbutmentRebarPlan? Analyze()
    {
        EnsureHostLive();
        ReviewedSnapshot = null;
        // Rebuild catalog each Analyze so Transfer Project Standards mid-session is visible.
        RefreshBarTypeCatalog();
        AbutmentDiagnostics.LogMessage(
            "REBAR_BAR_TYPE_CATALOG",
            _barTypeCatalog.IsEmpty
                ? "EMPTY"
                : ("count=" + _barTypeCatalog.Count + " | " + string.Join(", ", AvailableBarTypeNames)));
        Preset.RuleHash = Preset.ComputeHash();
        // Always re-extract so host/nested edits are not sticky within the session.
        AbutmentGeometryCache.Invalidate(_document);
        var extracted = AbutmentGeometryCache.GetOrExtract(_document, _host, Preset);
        Classification = new AbutmentClassifier().Classify(extracted, Preset);
        RefreshManagedReadbackCache();
        if (!RebarHostData.IsValidHost(_host))
        {
            Classification.Issues.Add(new ValidationIssue(
                "ABUTMENT_REBAR_HOST_INVALID",
                ValidationSeverity.Error,
                $"Host '{_host.Name}' ({_host.Id}) không thể tạo Rebar. Đây là lỗi family/project, " +
                "không phải thiếu RebarShape: bật Can Host Rebar, gán Structural Material bê tông/precast " +
                "trong family mố, reload family vào project, rồi Phân tích lại.",
                _host.Id));
        }
        if (Preset.Rules.Any(rule => rule.RequiresExistingRebarShape) &&
            !new FilteredElementCollector(_document).OfClass(typeof(RebarShape)).Any())
        {
            Classification.Issues.Add(new ValidationIssue(
                "REBAR_SHAPE_CATALOG_EMPTY",
                ValidationSeverity.Error,
                "Model chưa có RebarShape. Shape-driven Rebar chỉ có thể tạo shape mới khi document đã có " +
                "ít nhất một RebarShape đủ tham số; load/Transfer Project Standards Rebar Shapes rồi Phân tích lại.",
                _host.Id));
        }
        if (Classification.Status != AbutmentResolutionStatus.Resolved || Classification.Context is null)
        {
            CurrentPlan = null;
            ClearBoundZone();
            _uiDocument.RefreshActiveView();
            return null;
        }

        // Auto-bind the one canonical Footing zone produced from measured geometry.
        if (!TryBindCanonicalZone(Classification, preferredKind: null, out var bindError))
        {
            ClearBoundZone();
            CurrentPlan = null;
            Classification.Issues.Add(new ValidationIssue(
                "ABUTMENT_CANONICAL_ZONE_BIND_FAILED",
                ValidationSeverity.Error,
                bindError,
                _host.Id));
            _uiDocument.RefreshActiveView();
            return null;
        }

        if (_barTypeCatalog.IsEmpty)
        {
            Classification.Issues.Add(new ValidationIssue(
                "REBAR_BAR_TYPE_CATALOG_EMPTY",
                ValidationSeverity.Error,
                "Model chưa có RebarBarType. Hãy tạo/load type D12/D16/D20/D25/D32 trước khi rải thép mố.",
                _host.Id));
        }

        try
        {
            CurrentPlan = new AbutmentRebarPlanner(_document).Build(Classification, Preset);
            if (CurrentPlan is not null && Classification.Issues.Count > 0)
            {
                // Surface geometry/catalog diagnostics alongside plan issues.
                foreach (var issue in Classification.Issues)
                {
                    if (CurrentPlan.Issues.All(existing =>
                            !existing.Code.Equals(issue.Code, StringComparison.OrdinalIgnoreCase) ||
                            !existing.Message.Equals(issue.Message, StringComparison.Ordinal)))
                        CurrentPlan.Issues.Add(issue);
                }
            }
            if (CurrentPlan is not null)
            {
                var issueLines = CurrentPlan.Issues
                    .Select(issue => $"[{issue.Severity}] {issue.Code}: {issue.Message}")
                    .ToList();
                AbutmentDiagnostics.LogMessage(
                    "ABUTMENT_PLAN_ISSUES",
                    issueLines.Count == 0
                        ? $"planHash={CurrentPlan.PlanHash}; bars={CurrentPlan.Bars.Count}; NONE"
                        : $"planHash={CurrentPlan.PlanHash}; bars={CurrentPlan.Bars.Count}\n" +
                          string.Join("\n", issueLines));
            }
        }
        catch (Exception planException)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_PLAN_BUILD_FAILED", planException);
            CurrentPlan = null;
            Classification.Issues.Add(new ValidationIssue(
                "ABUTMENT_PLAN_BUILD_FAILED",
                ValidationSeverity.Error,
                "Lập kế hoạch thép thất bại: " + planException.GetBaseException().Message,
                _host.Id));
        }
        _uiDocument.RefreshActiveView();
        return CurrentPlan;
    }

    /// <summary>Binds exactly one canonical zone produced by classification.</summary>
    public bool TryBindCanonicalZone(
        AbutmentClassification classification,
        AbutmentZoneKind? preferredKind,
        out string error)
    {
        error = string.Empty;
        if (classification is not { Status: AbutmentResolutionStatus.Resolved, Context: not null })
        {
            error = "Chưa có classification hợp lệ để khóa canonical zone.";
            ClearBoundZone();
            return false;
        }

        var targetKind = preferredKind ?? AbutmentZoneKind.Footing;
        var candidates = classification.Zones
            .Where(zone => zone.Kind == targetKind)
            .ToList();
        if (candidates.Count != 1)
        {
            error = candidates.Count == 0
                ? $"Không có canonical zone {targetKind} từ geometry."
                : $"Có {candidates.Count} canonical zone {targetKind}; geometry đang mơ hồ.";
            ClearBoundZone();
            return false;
        }

        var context = classification.Context;
        return TryBindZoneEvidence(
            candidates[0],
            context.Host.UniqueId,
            context.GeometryHash,
            out error);
    }


    public AbutmentRebarPlan? ApplyRuleEdit(AbutmentRuleEdit edit)
        => ApplyRuleEdits([edit]);

    public AbutmentRebarPlan? ApplyRuleEdits(IEnumerable<AbutmentRuleEdit> edits)
    {
        EnsureHostLive();
        var editList = edits.ToList();
        if (editList.Count == 0) return CurrentPlan;
        var resolved = new List<(AbutmentZoneRule Rule, AbutmentRuleEdit Edit)>();
        foreach (var edit in editList)
        {
            var rule = Preset.Rules.SingleOrDefault(item =>
                           item.RuleId.Equals(edit.RuleId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"Không tìm thấy rule '{edit.RuleId}'.");
            if (!AvailableBarTypeNames.Contains(edit.BarTypeName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Rebar type '{edit.BarTypeName}' không tồn tại trong model.");
            if (!double.IsFinite(edit.DiameterMm) || edit.DiameterMm <= 0)
                throw new InvalidOperationException("Đường kính thanh phải là số hữu hạn lớn hơn 0.");
            if (rule.Enabled != edit.Enabled ||
                Math.Abs(rule.SpacingMm - edit.SpacingMm) > 1e-9 ||
                rule.FixedNumber != edit.FixedNumber ||
                !Nullable.Equals(rule.CoverOverrideMm, edit.CoverOverrideMm) ||
                rule.LayerOrder != edit.LayerOrder ||
                Math.Abs(rule.ClearGapToPreviousMm - edit.ClearGapToPreviousMm) > 1e-9 ||
                Math.Abs(rule.StartStationClearanceMm - edit.StartStationClearanceMm) > 1e-9 ||
                Math.Abs(rule.EndStationClearanceMm - edit.EndStationClearanceMm) > 1e-9)
                throw new InvalidOperationException(
                    "Chỉ được đổi RebarBarType (đường kính và mác thép). " +
                    "Enabled, spacing, cover, layer và khoảng lùi đang khóa theo hồ sơ.");
            resolved.Add((rule, edit));
        }

        _undo.Push(Preset.Clone());
        foreach (var (rule, edit) in resolved)
        {
            rule.BarTypeName = edit.BarTypeName.Trim();
            rule.DiameterMm = edit.DiameterMm;
        }
        return Analyze();
    }

    public AbutmentRebarPlan? UndoLastEdit()
    {
        if (_undo.Count == 0) return CurrentPlan;
        Preset = _undo.Pop();
        return Analyze();
    }

    public AbutmentRebarPlan? ResetWorkingPreset()
    {
        _undo.Push(Preset.Clone());
        Preset = _basePreset.Clone();
        return Analyze();
    }

    public string SaveAsUserProfile()
    {
        var saved = Preset.Clone();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        saved.ProfileId = $"{Preset.ProfileId}-USER-{timestamp}";
        saved.RuleVersion = timestamp;
        saved.DisplayName = $"{Preset.DisplayName} - tùy chỉnh {timestamp}";
        saved.RuleHash = saved.ComputeHash();
        var path = AbutmentPresetStore.SaveUserProfile(saved);
        var reloaded = AbutmentPresetStore.LoadAll().Single(profile =>
            profile.ProfileId.Equals(saved.ProfileId, StringComparison.OrdinalIgnoreCase));
        _basePreset = reloaded.Clone();
        Preset = reloaded.Clone();
        _undo.Clear();
        Analyze();
        return path;
    }

    public bool TryCapturePrewriteSnapshot(
        AbutmentZoneKind zone,
        out ReviewedAbutmentPlanSnapshot? snapshot,
        out string error)
    {
        ReviewedSnapshot = null;
        snapshot = null;
        error = string.Empty;
        if (!IsHostLive)
        {
            error = "Document hoặc host mố đã bị đóng/xóa sau khi mở công cụ; không chốt được " +
                    "snapshot để tạo thép. Đóng cửa sổ này và mở lại công cụ trên host mới.";
            return false;
        }
        if (!TryValidateCurrentSelection(Classification, out var selectionEvidence, out error) ||
            selectionEvidence.Kind != zone)
        {
            error = string.IsNullOrWhiteSpace(error)
                ? "Canonical geometry không khớp khu vực đang ghi."
                : error;
            return false;
        }
        var plan = CurrentPlan;
        if (plan is null || !plan.CanCreateZone(zone))
        {
            error = "Kế hoạch còn lỗi chặn; chưa thể capture pre-write snapshot.";
            return false;
        }
        if (!plan.Bars.Any(bar => bar.Zone == zone))
        {
            error = "Khu vực được chọn chưa có Rebar hợp lệ.";
            return false;
        }
        if (!plan.PlanHash.Equals(
                AbutmentRebarPlanner.ComputePlanHash(plan), StringComparison.OrdinalIgnoreCase))
        {
            error = "Plan hash đã thay đổi trong lúc capture pre-write snapshot.";
            return false;
        }
        foreach (var bar in plan.Bars.Where(item => item.Zone == zone))
        {
            if (_document.GetElement(bar.ResolvedBarTypeId) is not RebarBarType type ||
                !type.Name.Equals(bar.ResolvedBarTypeName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Rebar type '{bar.ResolvedBarTypeName}' không còn khớp model.";
                return false;
            }
        }

        snapshot = new ReviewedAbutmentPlanSnapshot
        {
            Plan = plan,
            Zone = zone,
            HostUniqueId = _host.UniqueId,
            HostId = _host.Id,
            GeometryHash = plan.GeometryHash,
            RuleHash = plan.Preset.RuleHash,
            PlanHash = plan.PlanHash,
            SelectionZoneCode = selectionEvidence.ZoneCode,
            SelectionFingerprint = AbutmentZoneSelectionKernel.ComputeFingerprint(selectionEvidence),
            ReviewedUtc = DateTime.UtcNow
        };
        ReviewedSnapshot = snapshot;
        return true;
    }

    public AbutmentCreateReceipt Execute(AbutmentWorkflowMode mode)
    {
        // Must come before anything else: FailureReceipt itself reads _document.PathName and
        // _host.UniqueId, so a blocked-receipt path is not available once the host is dead. Nothing
        // has been mutated at this point, and a receipt that cannot name its own document would be
        // worse evidence than none.
        EnsureHostLive();
        var snapshot = ReviewedSnapshot;
        if (snapshot is null)
            return FailureReceipt("ABUTMENT_REVIEWED_SNAPSHOT_MISSING",
                "Chưa có reviewed snapshot hợp lệ.");
        if (_host.Id != snapshot.HostId ||
            !_host.UniqueId.Equals(snapshot.HostUniqueId, StringComparison.Ordinal))
        {
            ReviewedSnapshot = null;
            return FailureReceipt("ABUTMENT_REVIEWED_HOST_CHANGED",
                "Host hiện tại không còn khớp host đã Review.");
        }
        if (!Preset.ComputeHash().Equals(snapshot.RuleHash, StringComparison.OrdinalIgnoreCase))
        {
            ReviewedSnapshot = null;
            return FailureReceipt("ABUTMENT_REVIEWED_RULE_CHANGED",
                "Cấu hình đã thay đổi sau Review.");
        }
        if (!TryValidateCurrentSelection(Classification, out var currentSelection, out _) ||
            currentSelection.Kind != snapshot.Zone ||
            !currentSelection.ZoneCode.Equals(snapshot.SelectionZoneCode, StringComparison.OrdinalIgnoreCase) ||
            !AbutmentZoneSelectionKernel.ComputeFingerprint(currentSelection)
                .Equals(snapshot.SelectionFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            ReviewedSnapshot = null;
            return FailureReceipt("ABUTMENT_REVIEWED_SELECTION_CHANGED",
                "Canonical zone đã thay đổi hoặc không còn khớp snapshot Review.");
        }

        AbutmentGeometryCache.Invalidate(_document);
        var extracted = AbutmentGeometryCache.GetOrExtract(_document, _host, snapshot.Plan.Preset);
        var fresh = new AbutmentClassifier().Classify(extracted, snapshot.Plan.Preset);
        if (fresh.Status != AbutmentResolutionStatus.Resolved ||
            fresh.Context is null ||
            !fresh.Context.GeometryHash.Equals(snapshot.GeometryHash, StringComparison.OrdinalIgnoreCase))
        {
            AbutmentDiagnostics.LogMessage(
                "ABUTMENT_REVIEWED_GEOMETRY_CHANGED",
                DescribeGeometryHashDrift(snapshot.Plan.Context, fresh.Context, fresh.Status));
            ReviewedSnapshot = null;
            return FailureReceipt("ABUTMENT_REVIEWED_GEOMETRY_CHANGED",
                "Hình học host đã thay đổi sau Review; hãy Phân tích và Review lại.");
        }
        if (!TryValidateCurrentSelection(fresh, out var freshSelection, out _) ||
            !AbutmentZoneSelectionKernel.ComputeFingerprint(freshSelection)
                .Equals(snapshot.SelectionFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            ReviewedSnapshot = null;
            return FailureReceipt("ABUTMENT_REVIEWED_SELECTION_STALE",
                "Canonical zone không còn khớp hình học vừa đọc lại; hãy Phân tích lại.");
        }

        var result = new AbutmentRebarFactory(_document).Execute(snapshot, mode);
        RefreshManagedReadbackCache();
        ReviewedSnapshot = null;
        if (result.Succeeded) _uiDocument.RefreshActiveView();
        return result;
    }

    private void RefreshManagedReadbackCache()
    {
        _managedPreviewCache.Clear();
        if (Classification?.Context is null) return;
        foreach (var zone in Enum.GetValues<AbutmentZoneKind>())
        {
            var preview = BuildManagedReadbackPreviewFromDocument(zone);
            if (preview is not null) _managedPreviewCache[zone] = preview;
        }
    }

    private static string DescribeGeometryHashDrift(
        AbutmentHostContext reviewed,
        AbutmentHostContext? fresh,
        AbutmentResolutionStatus freshStatus)
    {
        static string F(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static string Profiles(AbutmentHostContext context) => string.Join(" || ",
            context.ProfileMasses
                .OrderBy(item => item.Role)
                .ThenBy(item => item.StationCoordinate)
                .Select(item => string.Join("|",
                    item.Role,
                    item.FamilyName,
                    item.TypeName,
                    item.ParameterChecksum,
                    item.GeometryHash,
                    F(item.StationCoordinate),
                    F(item.MinX), F(item.MinY), F(item.MinZ),
                    F(item.MaxX), F(item.MaxY), F(item.MaxZ))));
        static string Solids(AbutmentHostContext context) => string.Join(" || ",
            context.Solids
                .OrderBy(item => ElementIdCompat.ToLong(item.HostId))
                .ThenBy(item => item.Volume)
                .Select(item => string.Join("|",
                    ElementIdCompat.ToLong(item.HostId), F(item.Volume),
                    F(item.LocalBox.Min.X), F(item.LocalBox.Min.Y), F(item.LocalBox.Min.Z),
                    F(item.LocalBox.Max.X), F(item.LocalBox.Max.Y), F(item.LocalBox.Max.Z))));
        static string Footing(AbutmentHostContext context) => context.FootingBoundary is null
            ? "<null>"
            : string.Join(";", context.FootingBoundary.Vertices.Select(point =>
                string.Join(",", F(point.X), F(point.Y), F(point.Z))));

        return fresh is null
            ? $"status={freshStatus}; reviewedGeometry={reviewed.GeometryHash}; freshContext=<null>; " +
              $"reviewedProfile={reviewed.ProfileMassHash}; reviewedProfiles={Profiles(reviewed)}"
            : $"status={freshStatus}; reviewedGeometry={reviewed.GeometryHash}; freshGeometry={fresh.GeometryHash}; " +
              $"reviewedProfile={reviewed.ProfileMassHash}; freshProfile={fresh.ProfileMassHash}; " +
              $"reviewedProfiles={Profiles(reviewed)}; freshProfiles={Profiles(fresh)}; " +
              $"reviewedSolids={Solids(reviewed)}; freshSolids={Solids(fresh)}; " +
              $"reviewedFooting={Footing(reviewed)}; freshFooting={Footing(fresh)}";
    }

    private AbutmentCreateReceipt FailureReceipt(string code, string message)
    {
        var receipt = new AbutmentCreateReceipt
        {
            Operation = "Blocked",
            GenerationId = Guid.NewGuid().ToString("N"),
            DocumentPath = _document.PathName,
            DocumentKey = $"{_document.Title}|{_document.PathName}",
            HostUniqueId = _host.UniqueId,
            RuleHash = Preset.RuleHash,
            GeometryHash = CurrentPlan?.GeometryHash ?? string.Empty,
            PlanHash = CurrentPlan?.PlanHash ?? string.Empty
        };
        receipt.Issues.Add(new ValidationIssue(code, ValidationSeverity.Error, message, _host.Id));
        AbutmentDiagnostics.LogMessage(code, message);
        AbutmentRebarFactory.PersistAuditReceipt(receipt);
        return receipt;
    }

    private bool TryValidateCurrentSelection(
        AbutmentClassification? classification,
        out AbutmentZoneSelectionEvidence evidence,
        out string error) =>
        TryValidateSelection(
            _boundZone,
            _boundZoneHostUniqueId,
            _boundZoneGeometryHash,
            classification,
            out evidence,
            out error);

    private static bool TryValidateSelection(
        AbutmentZoneDescriptor? selectedZone,
        string selectedHostUniqueId,
        string selectedGeometryHash,
        AbutmentClassification? classification,
        out AbutmentZoneSelectionEvidence evidence,
        out string error)
    {
        evidence = default;
        error = string.Empty;
        if (selectedZone is null ||
            classification is not { Status: AbutmentResolutionStatus.Resolved, Context: not null })
        {
            error = "Chưa có canonical zone và classification hợp lệ.";
            return false;
        }

        var canonicalZones = classification.Zones
            .Where(zone => zone.Kind == selectedZone.Kind)
            .ToList();
        if (canonicalZones.Count != 1)
        {
            error = canonicalZones.Count == 0
                ? "Khu vực chưa có canonical geometry để kiểm chứng selection."
                : "Có nhiều canonical geometry cùng loại; selection bị coi là mơ hồ.";
            return false;
        }

        var context = classification.Context;
        evidence = ToEvidence(
            selectedZone,
            selectedHostUniqueId,
            selectedGeometryHash);
        var canonical = ToEvidence(
            canonicalZones[0],
            context.Host.UniqueId,
            context.GeometryHash);
        var extent = Math.Max(context.OverallBox.Length,
            Math.Max(context.OverallBox.Depth, context.OverallBox.Height));
        return AbutmentZoneSelectionKernel.Validate(
            evidence,
            canonical,
            Math.Max(1e-6, extent * 1e-5),
            out error);
    }

    private static AbutmentZoneSelectionEvidence ToEvidence(
        AbutmentZoneDescriptor zone,
        string hostUniqueId,
        string geometryHash) => new(
        zone.Kind,
        zone.Code,
        hostUniqueId,
        geometryHash,
        zone.LocalBox.Min.X,
        zone.LocalBox.Min.Y,
        zone.LocalBox.Min.Z,
        zone.LocalBox.Max.X,
        zone.LocalBox.Max.Y,
        zone.LocalBox.Max.Z,
        zone.SupportingSolidIndexes.ToList());

    private void RefreshBarTypeCatalog()
    {
        _barTypeCatalog = new RebarTypeCatalog(_document);
        AlignPresetBarTypes(Preset);
        AlignPresetBarTypes(_basePreset);
    }

    private void AlignPresetBarTypes(AbutmentRebarPresetV1 preset)
    {
        if (_barTypeCatalog.IsEmpty) return;
        foreach (var rule in preset.Rules.Where(item => item.Enabled))
        {
            if (_barTypeCatalog.TryResolve(rule.BarTypeName, rule.DiameterMm, out var type, out _))
                rule.BarTypeName = type.Name;
        }
    }

    private static AbutmentZoneDescriptor CloneZone(AbutmentZoneDescriptor zone)
    {
        var clone = new AbutmentZoneDescriptor
        {
            Kind = zone.Kind,
            Code = zone.Code,
            LocalBox = new AbutmentLocalBox
            {
                Min = zone.LocalBox.Min,
                Max = zone.LocalBox.Max
            },
            SurfacePair = zone.SurfacePair is null
                ? null
                : new AbutmentZoneSurfacePair
                {
                    FrontFace = zone.SurfacePair.FrontFace,
                    BackFace = zone.SurfacePair.BackFace,
                    InnerFace = zone.SurfacePair.InnerFace,
                    OuterFace = zone.SurfacePair.OuterFace
                },
            ProfileGeometry = zone.ProfileGeometry is null
                ? null
                : new AbutmentProfileZoneGeometry
                {
                    Kind = zone.ProfileGeometry.Kind,
                    GeometryHash = zone.ProfileGeometry.GeometryHash,
                    Sections = zone.ProfileGeometry.Sections.Select(section =>
                        new AbutmentProfileZoneSection
                        {
                            Role = section.Role,
                            StationCoordinate = section.StationCoordinate,
                            OrderedPolygonLocal = section.OrderedPolygonLocal.ToArray()
                        }).ToArray()
                }
        };
        clone.SupportingSolidIndexes.AddRange(zone.SupportingSolidIndexes);
        return clone;
    }
}
