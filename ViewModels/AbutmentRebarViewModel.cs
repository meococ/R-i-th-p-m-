using System.Collections.ObjectModel;
using BIM.DatViet.Controllers;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;

namespace BIM.DatViet.ViewModels;

public sealed class AbutmentRebarViewModel : ObservableObject
{
    private static readonly HashSet<string> NonActionableTechnicalIssueCodes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ABUTMENT_EVIDENCE_HASH_MISMATCH",
        "ABUTMENT_TYPE_COMPATIBILITY_GEOMETRY_GATED",
        "ABUTMENT_FOOTING_ONLY_CAPABILITY"
        // SKEW / boundary / profile warnings must stay visible so operator sees why mats differ.
    };

    private static readonly string[] DesignIssueCodeFragments =
    [
        "ABUTMENT_RULE_",
        "ABUTMENT_ENABLED_RULES_EMPTY",
        "DRAWING_",
        "STANDARD_",
        "EVIDENCE_"
    ];

    private readonly AbutmentRebarController _controller;
    private readonly AbutmentRevitExternalEventDispatcher _revitDispatcher;
    private readonly Dispatcher _uiDispatcher;
    private string _headerStatusText = "Chưa phân tích kết cấu.";
    private string _selectedModelTitle = "Đang chờ phân tích phần tử mố cầu...";
    private string _designReadinessText = "Thiết kế: chưa phân tích";
    private string _runtimeReadinessText = "Runtime: chưa phân tích";
    private string _completionReadinessText = "Mức hoàn thiện: chưa phân tích";
    private double _coverTopMm;
    private double _coverBottomMm;
    private bool _lastOperationFailed;
    private bool _isBusy;
    private string _issueSummaryText = "Chưa có dữ liệu phân tích.";
    private string _previewNoticeText = "Nhấn Phân tích hình học để bắt đầu.";
    private string _actionReadinessText = "Hãy phân tích hình học mố cầu.";
    private string _layerSummaryText = string.Empty;
    private string _coverStatusText = string.Empty;
    private AbutmentZoneKind _selectedZoneKind = AbutmentZoneKind.Footing;
    private AbutmentZoneOptionViewModel? _selectedZoneOption;
    private AbutmentZoneDescriptor? _selectedZoneDescriptor;
    private string _selectionHostUniqueId = string.Empty;
    private string _selectionGeometryHash = string.Empty;
    private AbutmentActionGateResult _actionGate = new(
        AbutmentActionGateState.SelectZone,
        CanCreate: false,
        CanRebuild: false);
    private AbutmentViewportRenderModeOption? _selectedViewportRenderMode =
        AbutmentViewportRenderModeOption.XRay;

    public AbutmentRebarViewModel(
        AbutmentRebarController controller,
        AbutmentRevitExternalEventDispatcher revitDispatcher)
    {
        _controller = controller;
        _revitDispatcher = revitDispatcher;
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        AnalyzeCommand = new AsyncRelayCommand(
            () => RunBusyAsync("ABUTMENT_ANALYZE_UI_FAILED", ExecuteAnalyzeCore),
            () => !_isBusy);
        // Command predicates must stay side-effect free. Re-evaluating and notifying
        // from a CanExecute path previously caused a stack overflow in Revit.
        CreateCommand = new AsyncRelayCommand(
            () => RunBusyAsync("ABUTMENT_CREATE_UI_FAILED", ExecuteCreateCore),
            () => !_isBusy && _actionGate.CanCreate);
        RebuildCommand = new AsyncRelayCommand(
            () => RunBusyAsync("ABUTMENT_REBUILD_UI_FAILED", ExecuteRebuildCore),
            () => !_isBusy && _actionGate.CanRebuild);
        UndoCommand = new AsyncRelayCommand(
            () => RunBusyAsync("ABUTMENT_UNDO_UI_FAILED", ExecuteUndoCore),
            () => !_isBusy && _controller.CanUndo);

        InitializeInitialState();
    }

    public AbutmentRebarController Controller => _controller;
    public IReadOnlyList<string> AvailableBarTypeNames => _controller.AvailableBarTypeNames;
    public IReadOnlyList<AbutmentViewportRenderModeOption> ViewportRenderModes { get; } =
    [
        AbutmentViewportRenderModeOption.XRay,
        AbutmentViewportRenderModeOption.Wireframe
    ];
    public AbutmentViewportRenderMode ViewportRenderMode =>
        _selectedViewportRenderMode?.Mode ?? AbutmentViewportRenderMode.XRay;

    public ObservableCollection<AbutmentZoneOptionViewModel> AvailableZones { get; } = new();
    public ObservableCollection<RebarLayerItemViewModel> Layers { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public string HeaderStatusText
    {
        get => _headerStatusText;
        private set => SetProperty(ref _headerStatusText, value);
    }

    public string DesignReadinessText
    {
        get => _designReadinessText;
        private set => SetProperty(ref _designReadinessText, value);
    }

    public string RuntimeReadinessText
    {
        get => _runtimeReadinessText;
        private set => SetProperty(ref _runtimeReadinessText, value);
    }

    public string CompletionReadinessText
    {
        get => _completionReadinessText;
        private set => SetProperty(ref _completionReadinessText, value);
    }

    public string SelectedModelTitle
    {
        get => _selectedModelTitle;
        private set => SetProperty(ref _selectedModelTitle, value);
    }

    public double CoverTopMm
    {
        get => _coverTopMm;
        private set => SetProperty(ref _coverTopMm, value);
    }

    public double CoverBottomMm
    {
        get => _coverBottomMm;
        private set => SetProperty(ref _coverBottomMm, value);
    }

    public AbutmentZoneKind SelectedZoneKind
    {
        get => _selectedZoneKind;
        set
        {
            if (!SetProperty(ref _selectedZoneKind, value))
                return;
            SyncSelectedZoneOptionFromKind();
            OnZoneSelectionChanged(value);
        }
    }

    public AbutmentZoneOptionViewModel? SelectedZoneOption
    {
        get => _selectedZoneOption;
        set
        {
            if (SetProperty(ref _selectedZoneOption, value) && value is not null &&
                value.Kind != _selectedZoneKind)
            {
                // ComboBox drives Kind; avoid re-entry loops.
                _selectedZoneKind = value.Kind;
                OnPropertyChanged(nameof(SelectedZoneKind));
                OnZoneSelectionChanged(value.Kind);
            }
        }
    }

    public AbutmentViewportRenderModeOption? SelectedViewportRenderMode
    {
        get => _selectedViewportRenderMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedViewportRenderMode, value))
                return;
            RequestViewportRenderMode?.Invoke(value.Mode);
        }
    }

    public AbutmentZoneDescriptor? SelectedZoneDescriptor
    {
        get => _selectedZoneDescriptor;
        private set => SetProperty(ref _selectedZoneDescriptor, value);
    }

    public string IssueSummaryText
    {
        get => _issueSummaryText;
        private set => SetProperty(ref _issueSummaryText, value);
    }

    public string PreviewNoticeText
    {
        get => _previewNoticeText;
        private set => SetProperty(ref _previewNoticeText, value);
    }

    public string ActionReadinessText
    {
        get => _actionReadinessText;
        private set => SetProperty(ref _actionReadinessText, value);
    }

    public string LayerSummaryText
    {
        get => _layerSummaryText;
        private set => SetProperty(ref _layerSummaryText, value);
    }

    public string CoverStatusText
    {
        get => _coverStatusText;
        private set => SetProperty(ref _coverStatusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasCanonicalZone =>
        _controller.Classification is { Status: AbutmentResolutionStatus.Resolved } classification &&
        classification.Zones.Count(zone => zone.Kind == SelectedZoneKind) == 1;



    public AbutmentActionGateResult ActionGate => _actionGate;

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand CreateCommand { get; }
    public IAsyncRelayCommand RebuildCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }

    public event Action<AbutmentHostContext, IReadOnlyList<AbutmentZoneDescriptor>>? RequestViewportLoadScene;
    public event Action<AbutmentZoneKind, AbutmentRebarPlan?, bool>? RequestViewportHighlight;
    public event Action<AbutmentViewportRenderMode>? RequestViewportRenderMode;

    public void AnalyzeForInitialPreview() =>
        _ = RunBusyAsync("ABUTMENT_INITIAL_ANALYZE_UI_FAILED", ExecuteAnalyzeCore);

        public void ShowViewportLoadFailure(string message)
    {
        var text = string.IsNullOrWhiteSpace(message)
            ? "Không dựng được mô hình 3D."
            : message.Trim();
        PreviewNoticeText = "Viewport 3D lỗi: " + text;
        HeaderStatusText = "Phân tích có dữ liệu nhưng không dựng được 3D";
        UpdateIssues([
            new ValidationIssue(
                "ABUTMENT_VIEWPORT_LOAD_SCENE_FAILED",
                ValidationSeverity.Error,
                text,
                _controller.Host.Id)
        ]);
        UpdateActionGateState();
    }



    private void BindCanonicalZoneForCurrentView()
    {
        var classification = _controller.Classification;
        if (classification is not { Status: AbutmentResolutionStatus.Resolved, Context: not null })
        {
            ClearResolvedSelection();
            UpdateActionGateState();
            return;
        }

        if (!_controller.TryBindCanonicalZone(
                classification,
                SelectedZoneKind,
                out var error))
        {
            PreviewNoticeText = string.IsNullOrWhiteSpace(error)
                ? "Chưa khóa được canonical zone từ hình học mố."
                : error;
            ClearResolvedSelection();
            UpdateActionGateState();
            return;
        }

        SelectedZoneDescriptor = _controller.CurrentBoundZone;
        _selectionHostUniqueId = classification.Context.Host.UniqueId;
        _selectionGeometryHash = classification.Context.GeometryHash;
        
        PreviewNoticeText =
            $"Đã khóa {ZoneDisplayName(SelectedZoneKind)} từ hình học thực tế.";
        UpdateActionGateState();
        RequestViewportHighlight?.Invoke(
            SelectedZoneKind,
            ResolveViewportPlan(_controller.CurrentPlan),
            true);
    }


    private void InitializeInitialState()
    {
        RefreshZoneOptions();
        RefreshLayersForSelectedZone();
        UpdateActionGateState();
    }

    private void ExecuteAnalyzeCore()
    {
        
        ClearResolvedSelection();
        var plan = _controller.Analyze();
        var classification = _controller.Classification;
        RefreshZoneOptions();
        RefreshLayersForSelectedZone();

        if (classification is null)
        {
            HeaderStatusText = "Phân tích thất bại: Chưa rõ";
            SelectedModelTitle = "Không thể nhận diện hình học mố.";
            PreviewNoticeText = "Không có kết quả phân tích. Xem log nếu cần.";
            return;
        }

        // Paint host geometry whenever extraction produced a context; blocking issues stay visible.
        if (classification.Context is not null)
        {
            HeaderStatusText = classification.Status == AbutmentResolutionStatus.Resolved
                ? $"Đã nhận diện: {classification.Context.Host.Name}"
                : $"Phân tích: {classification.Status} — {classification.Context.Host.Name}";
            SelectedModelTitle =
                $"Mố ID: {classification.Context.Host.Id} | Canonical zone: {classification.Zones.Count} | " +
                $"PROFILE evidence: {classification.Context.ProfileMasses.Count} | " +
                $"Profile: {_controller.Preset.ProfileId} v{_controller.Preset.RuleVersion}";
            RequestViewportLoadScene?.Invoke(classification.Context, classification.Zones);
        }
        else
        {
            HeaderStatusText = "Phân tích thất bại: " + classification.Status;
            SelectedModelTitle = "Không thể nhận diện hình học mố.";
        }

        var forcedPreviewNotice = string.Empty;
        if (plan is not null)
        {
            // Keep layer labels aligned with any diameter-resolved type names.
            RefreshLayersForSelectedZone();
            UpdateQuantitiesFromPlan(plan);
            OnPropertyChanged(nameof(AvailableBarTypeNames));
            UpdateIssues(plan.Issues);
            var hostIssue = plan.Issues.FirstOrDefault(issue =>
                issue.Code.Equals("ABUTMENT_REBAR_HOST_INVALID", StringComparison.OrdinalIgnoreCase));
            if (hostIssue is not null)
            {
                forcedPreviewNotice = hostIssue.Message;
                HeaderStatusText = "Host mố chưa sẵn sàng tạo Rebar";
            }
            else if (plan.Issues.FirstOrDefault(issue =>
                         issue.Code.Equals("REBAR_SHAPE_CATALOG_EMPTY", StringComparison.OrdinalIgnoreCase))
                     is { } shapeIssue)
            {
                forcedPreviewNotice = shapeIssue.Message;
                HeaderStatusText = "Model thiếu RebarShape";
            }
            else if (!_controller.HasAnyRebarBarType)
            {
                forcedPreviewNotice =
                    "Model chưa có RebarBarType. Tạo/load D12/D16/D20/D25/D32 rồi Phân tích lại.";
                HeaderStatusText = "Thiếu RebarBarType trong model";
            }
            else if (!HasEnabledRuleForSelectedZone())
            {
                var selectedRules = _controller.Preset.Rules
                    .Where(rule => rule.Zone == SelectedZoneKind)
                    .ToList();
                var lockedRules = selectedRules.Where(rule => !rule.Enabled).ToList();
                var recognized = classification.Zones.Any(zone => zone.Kind == SelectedZoneKind);
                var categories = CollectBlockerCategories(plan.Issues, lockedRules);
                HeaderStatusText = recognized
                    ? $"Đã nhận diện nhưng khóa: {ZoneDisplayName(SelectedZoneKind)} ({lockedRules.Count} mark)"
                    : $"Chưa nhận diện và đang khóa: {ZoneDisplayName(SelectedZoneKind)}";
                forcedPreviewNotice =
                    $"{ZoneDisplayName(SelectedZoneKind)} " +
                    (recognized ? "đã nhận diện geometry nhưng" : "chưa nhận diện geometry và") +
                    $" có {lockedRules.Count}/{selectedRules.Count} mark khóa; Create/Rebuild bị khóa. " +
                    $"Nhóm blocker: {FormatCategories(categories)}. " +
                    "Lý do chính xác của từng mark hiển thị trong danh sách lớp thép.";
            }
            else if (plan.Bars.Count == 0)
            {
                var typeErrors = plan.Issues
                    .Where(i => i.Code.Contains("BAR_TYPE", StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.Message)
                    .Take(2)
                    .ToList();
                if (typeErrors.Count > 0)
                {
                    forcedPreviewNotice = string.Join(" | ", typeErrors);
                    HeaderStatusText = "Không khớp RebarBarType trong model";
                }
            }
        }
        else
        {
            UpdateIssues(classification.Issues);
        }

        // Open → Analyze → canonical zone bind → show proposed bars. No pick step.
        if (classification.Context is not null)
        {
            if (classification.Status == AbutmentResolutionStatus.Resolved)
            {
                BindCanonicalZoneForCurrentView();
            }

            if (!string.IsNullOrWhiteSpace(forcedPreviewNotice))
            {
                var viewportPlan = ResolveViewportPlan(plan);
                var managedCount = viewportPlan?.PlanHash == "MANAGED-READBACK"
                    ? viewportPlan.Bars.Count(bar => bar.Zone == SelectedZoneKind)
                    : 0;
                PreviewNoticeText = forcedPreviewNotice +
                                    (managedCount > 0
                                        ? $" Viewport đang đọc lại {managedCount} Rebar hiện có; đây không phải đề xuất Create."
                                        : string.Empty);
                if (viewportPlan is not null)
                    RequestViewportHighlight?.Invoke(SelectedZoneKind, viewportPlan, true);
            }
            else if (SelectedZoneDescriptor is not null && plan is not null)
            {
                var barCount = plan.Bars.Count(b => b.Zone == SelectedZoneKind);
                PreviewNoticeText =
                    $"Đề xuất {barCount} thanh (zone {SelectedZoneDescriptor.Code}). " +
                    "Xoay/zoom để kiểm tra trước khi tạo.";
                RequestViewportHighlight?.Invoke(SelectedZoneKind, plan, true);
            }
            else if (plan is not null)
            {
                PreviewNoticeText =
                    $"Đã lập kế hoạch {plan.Bars.Count} thanh nhưng chưa bind zone. Xem issues.";
                RequestViewportHighlight?.Invoke(SelectedZoneKind, plan, true);
            }
            else
            {
                // Still outline footing band when plan failed — operator sees cut zone.
                if (SelectedZoneDescriptor is not null)
                    RequestViewportHighlight?.Invoke(SelectedZoneKind, null, false);
                var err = classification.Issues
                    .Where(i => i.Severity == ValidationSeverity.Error)
                    .Select(i => i.Code)
                    .Distinct()
                    .ToList();
                PreviewNoticeText = err.Count > 0
                    ? "Chưa lập được thép đề xuất. Lỗi: " + string.Join(", ", err) + ". Xem danh sách dưới."
                    : (classification.Status == AbutmentResolutionStatus.Resolved
                        ? CurrentZoneStatus()
                        : "Chưa lập được kế hoạch thép. Xem danh sách lỗi hình học.");
            }
        }
    }

    private void ApplyLayerEditsCore()
    {
        var edits = Layers
            .Where(layer => layer.IsSupported)
            .Select(layer => layer.ToEdit())
            .ToList();
        var plan = _controller.ApplyRuleEdits(edits);
        RefreshZoneOptions();
        if (plan is null)
        {
            UpdateIssues(_controller.Classification?.Issues ?? []);
            DeriveCoverDrafts();
            return;
        }

        UpdateQuantitiesFromPlan(plan);
            OnPropertyChanged(nameof(AvailableBarTypeNames));
        UpdateIssues(plan.Issues);
        DeriveCoverDrafts();
        RequestViewportHighlight?.Invoke(SelectedZoneKind, plan, true);
    }

    private void ExecuteCreateCore() => ExecuteWriteCore(AbutmentWorkflowMode.Create);

    private void ExecuteRebuildCore() => ExecuteWriteCore(AbutmentWorkflowMode.RebuildZone);

    private void ExecuteWriteCore(AbutmentWorkflowMode mode)
    {
        // Capture and validate one immutable pre-write snapshot immediately before
        // the intentional Create/Rebuild action.
        if (!_controller.TryCapturePrewriteSnapshot(SelectedZoneKind, out _, out var error))
        {
            UpdateIssues([
                new ValidationIssue("ABUTMENT_PREWRITE_VALIDATION_FAILED",
                    ValidationSeverity.Error, error)
            ]);
            _lastOperationFailed = true;
            _controller.InvalidateReviewedSnapshot();
            HeaderStatusText = "Không ghi được thép: pre-write validation thất bại.";
            ActionReadinessText = string.IsNullOrWhiteSpace(error)
                ? "Chưa đủ điều kiện Create/Rebuild."
                : error;
            return;
        }

        var receipt = _controller.Execute(mode);
        UpdateIssues(receipt.Issues);

        if (receipt.Succeeded)
        {
            var quantity = receipt.CreatedIds.Count;
            var verb = mode == AbutmentWorkflowMode.RebuildZone ? "Rebuild" : "Create";
            HeaderStatusText = $"{verb} thành công: {quantity} rebar zone {SelectedZoneKind}.";
            PreviewNoticeText =
                $"{verb} xong. Ownership metadata đã ghi. Có thể Rebuild nếu cần chỉnh lại.";
            RequestViewportHighlight?.Invoke(SelectedZoneKind, _controller.CurrentPlan, true);
        }
        else
        {
            _lastOperationFailed = true;
            _controller.InvalidateReviewedSnapshot();
            var firstError = receipt.Issues
                .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error);
            HeaderStatusText = mode == AbutmentWorkflowMode.RebuildZone
                ? "Rebuild thất bại."
                : "Create thất bại.";
            ActionReadinessText = firstError?.Message
                ?? "Ghi thép bị chặn. Xem danh sách lỗi bên dưới.";
        }
    }

    private void ExecuteUndoCore()
    {
        var plan = _controller.UndoLastEdit();
        
        RefreshZoneOptions();
        RefreshLayersForSelectedZone();
        if (plan is not null)
        {
            UpdateQuantitiesFromPlan(plan);
            OnPropertyChanged(nameof(AvailableBarTypeNames));
            UpdateIssues(plan.Issues);
        }
        else
        {
            UpdateIssues(_controller.Classification?.Issues ?? []);
        }
    }


    private void OnRuleItemChanged()
    {
        if (_isBusy)
        {
            return;
        }

        
        _ = RunBusyAsync("ABUTMENT_RULE_EDIT_UI_FAILED", () =>
        {
            _controller.InvalidateReviewedSnapshot();
            ApplyLayerEditsCore();
        });
    }

    private void OnZoneSelectionChanged(AbutmentZoneKind zone)
    {
        
        ClearResolvedSelection();
        RefreshLayersForSelectedZone();
        BindCanonicalZoneForCurrentView();
        if (SelectedZoneDescriptor is null)
        {
            PreviewNoticeText = HasCanonicalZone
                ? $"Đã chọn {ZoneDisplayName(zone)}; đang khóa lại canonical zone."
                : CurrentZoneStatus();
        }
        UpdateActionGateState();
    }


    private void RefreshZoneOptions()
    {
        var classification = _controller.Classification;
        var configuredZones = ConfiguredZoneKinds()
            .OrderBy(ZoneSortOrder)
            .ToList();
        if (configuredZones.Count == 0)
            configuredZones.Add(AbutmentZoneKind.Footing);

        var recognized = classification?.Zones.Select(zone => zone.Kind).ToHashSet()
            ?? new HashSet<AbutmentZoneKind>();

        var preferred = _selectedZoneKind;
        AvailableZones.Clear();
        foreach (var zone in configuredZones)
        {
            var isRecognized = recognized.Contains(zone);
            var zoneRules = _controller.Preset.Rules.Where(rule => rule.Zone == zone).ToList();
            var enabledCount = zoneRules.Count(rule => rule.Enabled);
            var lockedRules = zoneRules.Where(rule => !rule.Enabled).ToList();
            var hasEnabledRules = enabledCount > 0;
            var provenance = ResolveZoneGeometryProvenance(zone);
            var ruleStatus = zoneRules.Count == 0
                ? "không có rule cấu hình"
                : lockedRules.Count == 0
                    ? $"{enabledCount} mark mở"
                    : $"{enabledCount} mark mở • {lockedRules.Count} mark khóa — " +
                      FormatLockedRuleReasons(lockedRules);
            var status = isRecognized
                ? $"Đã nhận diện • {provenance} • {ruleStatus}"
                : $"Chưa nhận diện canonical geometry • {provenance} • {ruleStatus}";
            AvailableZones.Add(new AbutmentZoneOptionViewModel(
                zone,
                ZoneDisplayName(zone),
                status,
                isRecognized,
                hasEnabledRules));
        }

        if (AvailableZones.Count > 0 && AvailableZones.All(item => item.Kind != preferred))
            preferred = AvailableZones.FirstOrDefault(item => item.Kind == AbutmentZoneKind.Footing)?.Kind
                        ?? AvailableZones[0].Kind;

        // Rebind ComboBox SelectedItem after ItemsSource rebuild (WPF blank selection fix).
        _selectedZoneKind = preferred;
        _selectedZoneOption = AvailableZones.FirstOrDefault(item => item.Kind == preferred);
        OnPropertyChanged(nameof(SelectedZoneKind));
        OnPropertyChanged(nameof(SelectedZoneOption));
        OnPropertyChanged(nameof(HasCanonicalZone));
    }

    private void SyncSelectedZoneOptionFromKind()
    {
        var match = AvailableZones.FirstOrDefault(item => item.Kind == _selectedZoneKind);
        if (!ReferenceEquals(_selectedZoneOption, match))
        {
            _selectedZoneOption = match;
            OnPropertyChanged(nameof(SelectedZoneOption));
        }
    }

    private void RefreshLayersForSelectedZone()
    {
        Layers.Clear();
        foreach (var rule in AbutmentActionGate.VisibleRulesForZone(
                     _controller.Preset.Rules,
                     SelectedZoneKind))
        {
            var item = new RebarLayerItemViewModel(rule, _controller.GetBarTypeDiameterMm)
            {
                OnChanged = OnRuleItemChanged
            };
            Layers.Add(item);
        }
        var enabled = Layers.Count(layer => layer.IsEnabled);
        var locked = Layers.Count(layer => !layer.IsEnabled);

        LayerSummaryText = Layers.Count == 0
            ? "Chưa có lớp thép đủ thông tin để cấu hình cho khu vực này."
            : $"{Layers.Count} mark cấu hình • {enabled} mở • {locked} khóa; " +
              "xem lý do chính xác trên từng mark";
        DeriveCoverDrafts();
    }

    private void DeriveCoverDrafts()
    {
        var topValues = Layers
            .Where(layer => layer.IsSupported && layer.IsEnabled &&
                            layer.OriginalRule.FaceRole == AbutmentFaceRole.Top &&
                            layer.CoverOverrideMm is > 0)
            .Select(layer => layer.CoverOverrideMm!.Value)
            .Distinct()
            .ToList();
        var bottomValues = Layers
            .Where(layer => layer.IsSupported && layer.IsEnabled &&
                            layer.OriginalRule.FaceRole == AbutmentFaceRole.Bottom &&
                            layer.CoverOverrideMm is > 0)
            .Select(layer => layer.CoverOverrideMm!.Value)
            .Distinct()
            .ToList();

        CoverTopMm = topValues.Count == 1 ? topValues[0] : 0;
        CoverBottomMm = bottomValues.Count == 1 ? bottomValues[0] : 0;
        CoverStatusText = topValues.Count > 1 || bottomValues.Count > 1
            ? "Các lớp đang có nhiều giá trị cover trong preset; Create đang khóa."
            : "Cover là giá trị preset dự kiến; Create chỉ mở khi đủ nguồn hoặc phê duyệt.";
    }

    private void UpdateQuantitiesFromPlan(AbutmentRebarPlan plan)
    {
        foreach (var layer in Layers)
        {
            var matchingBars = plan.Bars.Where(bar => bar.Rule.RuleId == layer.RuleId).ToList();
            if (!layer.IsEnabled)
            {
                layer.PlannedQuantityText = "—";
            }
            else if (matchingBars.Count > 0)
            {
                // A spliced layer holds two rebar objects per bar position, so the two counts have to
                // be shown apart: the drawing counts positions, the model holds pieces.
                var positions = matchingBars.Select(bar => bar.PositionIndex).Distinct().Count();
                var objects = matchingBars.Sum(bar => bar.PlannedQuantity);
                layer.PlannedQuantityText = objects == positions
                    ? $"{objects:N0} thanh"
                    : $"{positions:N0} thanh, {objects:N0} đoạn";
            }
            else
            {
                layer.PlannedQuantityText = "0 thanh";
            }
        }
    }

    private void UpdateIssues(IEnumerable<ValidationIssue> issues)
    {
        var allIssues = issues.ToList();
        var visibleIssues = allIssues
            .Where(issue =>
                !issue.Code.Equals("ABUTMENT_DRAWING_RULE_DISABLED", StringComparison.OrdinalIgnoreCase) &&
                (issue.Severity == ValidationSeverity.Error ||
                 !NonActionableTechnicalIssueCodes.Contains(issue.Code)))
            .ToList();
        var disabledRules = _controller.Preset.Rules.Where(rule => !rule.Enabled).ToList();
        var enabledRuleCount = _controller.Preset.Rules.Count(rule => rule.Enabled);
        var blockerCategories = new SortedSet<string>(
            CollectBlockerCategories(allIssues, disabledRules),
            StringComparer.OrdinalIgnoreCase);
        foreach (var zone in ConfiguredZoneKinds())
        {
            if (_controller.Preset.Rules.All(rule => rule.Zone != zone))
                blockerCategories.Add("rule chưa cấu hình");
            if (_controller.Classification?.Zones.Count(item => item.Kind == zone) != 1)
                blockerCategories.Add("canonical geometry chưa nhận diện");
            if (!ResolveZoneGeometryProvenance(zone).StartsWith("PROFILE authority", StringComparison.Ordinal))
                blockerCategories.Add("profile unresolved");
        }
        if (disabledRules.Count > 0)
        {
            visibleIssues.Add(new ValidationIssue(
                "ABUTMENT_DRAWING_RULE_DISABLED",
                ValidationSeverity.Warning,
                $"Tóm tắt source matrix: {enabledRuleCount} mark mở, {disabledRules.Count} mark khóa. " +
                $"Nhóm blocker: {FormatCategories(blockerCategories)}. " +
                "Chọn từng khu vực để xem lý do chính xác của mỗi mark."));
        }
        Issues.Clear();
        foreach (var issue in visibleIssues)
        {
            Issues.Add(issue);
        }

        var errors = allIssues.Count(issue => issue.Severity == ValidationSeverity.Error);
        var designErrors = allIssues.Count(issue =>
            issue.Severity == ValidationSeverity.Error &&
            DesignIssueCodeFragments.Any(fragment =>
                issue.Code.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        var runtimeErrors = errors - designErrors;
        var completion = EvaluateCompletionCoverage();
        DesignReadinessText = designErrors > 0
            ? $"Thiết kế: khóa ({designErrors} lỗi)"
            : completion.IsFull
                ? "Thiết kế: đầy đủ"
                : "Thiết kế: một phần";
        RuntimeReadinessText = runtimeErrors == 0
            ? "Runtime: đạt"
            : $"Runtime: khóa ({runtimeErrors} lỗi)";
        var warnings = visibleIssues.Count(issue => issue.Severity == ValidationSeverity.Warning);
        var infos = visibleIssues.Count(issue => issue.Severity == ValidationSeverity.Info);
        var technicalSummary = errors > 0
            ? $"Có {errors} lỗi cần xử lý" + (warnings > 0 ? $" và {warnings} lưu ý." : ".")
            : warnings > 0
                ? $"Kế hoạch hợp lệ • {warnings} lưu ý cần xem."
                : infos > 0
                    ? $"Kế hoạch hợp lệ • {infos} kiểm tra kỹ thuật đã ghi nhận; không có cảnh báo chặn Create."
                    : "Kế hoạch hợp lệ • không có cảnh báo cần xử lý.";
        IssueSummaryText = technicalSummary +
                           $" Source matrix: {enabledRuleCount} mark mở, {disabledRules.Count} mark khóa; " +
                           $"blocker: {FormatCategories(blockerCategories)}.";
        CompletionReadinessText = completion.IsFull
            ? $"Mức hoàn thiện: đầy đủ • {completion.CoveredZones}/{completion.TotalZones} zone • " +
              $"{enabledRuleCount} mark mở/{disabledRules.Count} khóa"
            : $"Mức hoàn thiện: một phần • {completion.CoveredZones}/{completion.TotalZones} zone có PROFILE + rule mở • " +
              $"{enabledRuleCount} mark mở/{disabledRules.Count} khóa • blocker: {FormatCategories(blockerCategories)}";
    }

    private string ResolveZoneGeometryProvenance(AbutmentZoneKind zone)
    {
        var classification = _controller.Classification;
        var descriptor = classification?.Zones.FirstOrDefault(item => item.Kind == zone);
        var context = classification?.Context;
        var hasProfileAuthority = zone == AbutmentZoneKind.Footing
            ? context?.ProfileFooting is not null && context.FootingBoundary is not null
            : descriptor?.ProfileGeometry is not null;
        if (hasProfileAuthority)
            return "PROFILE authority • SOLID validation-only";
        return descriptor is not null
            ? "SOLID fallback/review • profile unresolved"
            : "profile unresolved";
    }

    private (bool IsFull, int CoveredZones, int TotalZones) EvaluateCompletionCoverage()
    {
        var configuredZones = ConfiguredZoneKinds().ToList();
        if (configuredZones.Count == 0) return (false, 0, 0);

        var covered = 0;
        var full = true;
        foreach (var zone in configuredZones)
        {
            var rules = _controller.Preset.Rules.Where(rule => rule.Zone == zone).ToList();
            var recognized = _controller.Classification?.Zones.Count(item => item.Kind == zone) == 1;
            var profileAuthority = ResolveZoneGeometryProvenance(zone)
                .StartsWith("PROFILE authority", StringComparison.Ordinal);
            if (recognized && profileAuthority && rules.Any(rule => rule.Enabled)) covered++;
            if (!recognized || !profileAuthority || rules.Count == 0 || rules.Any(rule => !rule.Enabled))
                full = false;
        }
        return (full, covered, configuredZones.Count);
    }

    private IReadOnlyList<AbutmentZoneKind> ConfiguredZoneKinds()
    {
        var topology = _controller.Preset.Topology;
        var configured = _controller.Preset.Rules.Select(rule => rule.Zone)
            .Concat(topology.RequiredZones)
            .Concat(_controller.Classification?.Zones.Select(zone => zone.Kind) ?? [])
            .ToHashSet();
        var hasWingConfiguration = topology.WingwallLengthMm > 0 ||
                                   topology.WingwallThicknessMm > 0 ||
                                   topology.WingLengthLeftParameterNames.Count > 0 ||
                                   topology.WingLengthRightParameterNames.Count > 0 ||
                                   topology.WingLengthParameterNames.Count > 0 ||
                                   topology.WingInnerLengthLeftParameterNames.Count > 0 ||
                                   topology.WingInnerLengthRightParameterNames.Count > 0 ||
                                   topology.WingOuterLengthLeftParameterNames.Count > 0 ||
                                   topology.WingOuterLengthRightParameterNames.Count > 0;
        if (hasWingConfiguration)
        {
            configured.Add(AbutmentZoneKind.WingwallLeft);
            configured.Add(AbutmentZoneKind.WingwallRight);
        }
        return configured.ToList();
    }

    private static string FormatLockedRuleReasons(IEnumerable<AbutmentZoneRule> rules) =>
        string.Join(" | ", rules.Select(rule =>
            $"{rule.CanonicalMark}: " +
            (string.IsNullOrWhiteSpace(rule.DisabledReason)
                ? "chưa ghi lý do khóa"
                : rule.DisabledReason.Trim())));

    private static IReadOnlyList<string> CollectBlockerCategories(
        IEnumerable<ValidationIssue> issues,
        IEnumerable<AbutmentZoneRule> disabledRules)
    {
        var categories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in issues.Where(issue => issue.Severity != ValidationSeverity.Info))
            AddCategories(issue.Code + " " + issue.Message, categories);
        foreach (var rule in disabledRules)
            AddCategories(rule.DisabledReason, categories);
        return categories.ToList();
    }

    private static void AddCategories(string value, ISet<string> categories)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            categories.Add("lý do khóa chưa khai báo");
            return;
        }
        var text = value.ToUpperInvariant();
        var matched = false;
        Add("PROFILE", "PROFILE/canonical geometry");
        Add("WING", "profile/handedness tường cánh");
        Add("SOURCE", "nguồn và phê duyệt");
        Add("MATRIX", "nguồn và phê duyệt");
        Add("HỒ SƠ", "nguồn và phê duyệt");
        Add("CHỜ", "nguồn và phê duyệt");
        Add("COVER", "cover");
        Add("STATION", "station/spacing");
        Add("SPACING", "station/spacing");
        Add("KHOẢNG", "station/spacing");
        Add("PATH", "path/shape/hook/development");
        Add("SHAPE", "path/shape/hook/development");
        Add("HOOK", "path/shape/hook/development");
        Add("TERMINATION", "path/shape/hook/development");
        Add("DEVELOPMENT", "path/shape/hook/development");
        Add("HAI ĐẦU", "path/shape/hook/development");
        Add("FACE", "face/layer semantics");
        Add("LAYER", "face/layer semantics");
        Add("PILE", "pile/obstacle policy");
        Add("CỌC", "pile/obstacle policy");
        Add("OBSTACLE", "pile/obstacle policy");
        Add("BAR_TYPE", "RebarBarType");
        Add("REBAR HOST", "Rebar host/runtime");
        Add("HOST_INVALID", "Rebar host/runtime");
        if (!matched) categories.Add("khác");
        return;

        void Add(string token, string category)
        {
            if (!text.Contains(token, StringComparison.OrdinalIgnoreCase)) return;
            categories.Add(category);
            matched = true;
        }
    }

    private static string FormatCategories(IReadOnlyCollection<string> categories) =>
        categories.Count == 0 ? "không có" : string.Join(", ", categories);

    private bool HasValidViewportSelection()
    {
        var context = _controller.Classification?.Context;
        return SelectedZoneDescriptor is not null &&
               SelectedZoneDescriptor.Kind == SelectedZoneKind &&
               context is not null &&
               context.Host.UniqueId.Equals(_selectionHostUniqueId, StringComparison.Ordinal) &&
               context.GeometryHash.Equals(_selectionGeometryHash, StringComparison.OrdinalIgnoreCase) &&
               _controller.HasBoundZone(SelectedZoneKind);
    }

    private bool HasEnabledRuleForSelectedZone() =>
        _controller.Preset.Rules.Any(rule => rule.Zone == SelectedZoneKind && rule.Enabled);

    private AbutmentActionGateResult EvaluateActionGate()
    {
        var plan = _controller.CurrentPlan;
        var hasZoneChoice = AvailableZones.Any(item => item.Kind == SelectedZoneKind);
        var input = new AbutmentActionGateInput(
            HasZoneChoice: hasZoneChoice,
            HasCanonicalZone: HasValidViewportSelection(),
            HasEnabledRule: HasEnabledRuleForSelectedZone(),
            PlanCanCreate: !_lastOperationFailed && plan is not null && plan.CanCreateZone(SelectedZoneKind),
            HasPlannedBars: plan is not null && plan.Bars.Any(bar => bar.Zone == SelectedZoneKind),
            HasManagedRebars: hasZoneChoice && _controller.HasManagedRebars(SelectedZoneKind));
        return AbutmentActionGate.Evaluate(input);
    }

    private void UpdateActionGateState()
    {
        _actionGate = EvaluateActionGate();
        ActionReadinessText = GetActionReadinessMessage(_actionGate);
        OnPropertyChanged(nameof(ActionGate));
        NotifyCommandStates();
    }

    private AbutmentRebarPlan? ResolveViewportPlan(AbutmentRebarPlan? planned)
    {
        if (planned is not null && planned.Bars.Any(bar => bar.Zone == SelectedZoneKind))
            return planned;
        return _controller.BuildManagedReadbackPreview(SelectedZoneKind) ?? planned;
    }

    private string GetActionReadinessMessage(AbutmentActionGateResult gate)
    {
        if (_lastOperationFailed)
            return "Thao tác vừa thất bại; kế hoạch cũ đã bị khóa. Hãy Phân tích hình học lại.";
        var readinessBlocker = _controller.CurrentPlan?.Issues.FirstOrDefault(issue =>
            issue.Severity == ValidationSeverity.Error &&
            (issue.Code.Equals("ABUTMENT_REBAR_HOST_INVALID", StringComparison.OrdinalIgnoreCase) ||
             issue.Code.Equals("REBAR_SHAPE_CATALOG_EMPTY", StringComparison.OrdinalIgnoreCase)));
        if (readinessBlocker is not null) return readinessBlocker.Message;
        if (!_controller.HasAnyRebarBarType)
            return "Model chưa có RebarBarType. Hãy tạo/load type D12/D16/D20/D25/D32 rồi Phân tích lại.";
        return gate.State switch
        {
            AbutmentActionGateState.SelectZone => "Chọn một khu vực kết cấu.",
            AbutmentActionGateState.NoApprovedRule => GetNoApprovedRuleMessage(),
            AbutmentActionGateState.BindCanonicalZone =>
                $"Chưa khóa được canonical zone {ZoneDisplayName(SelectedZoneKind)}. Bấm Phân tích lại sau khi chọn đúng host mố.",
            AbutmentActionGateState.ResolveBlockingIssues =>
                "Kế hoạch có lỗi chặn; bổ sung dữ liệu/phê duyệt hoặc chọn RebarBarType phù hợp, rồi Phân tích lại.",
            AbutmentActionGateState.MissingPlannedBars =>
                "Khu vực chưa sinh được đường thép hợp lệ.",
            AbutmentActionGateState.ReadyCreate =>
                $"Sẵn sàng tạo Rebar mới cho {ZoneDisplayName(SelectedZoneKind)}.",
            AbutmentActionGateState.ReadyRebuild =>
                $"{ZoneDisplayName(SelectedZoneKind)} đã có Rebar do ĐVB quản lý; sẵn sàng tạo lại khu vực.",
            _ => "Chưa sẵn sàng."
        };
    }

    private string GetNoApprovedRuleMessage()
    {
        var lockedRules = _controller.Preset.Rules
            .Where(rule => rule.Zone == SelectedZoneKind && !rule.Enabled)
            .ToList();
        if (lockedRules.Count == 0)
            return $"{ZoneDisplayName(SelectedZoneKind)} chưa có rule cấu hình cho Create.";
        var recognition = _controller.Classification?.Zones.Count(zone => zone.Kind == SelectedZoneKind) == 1
            ? "đã nhận diện nhưng"
            : "chưa nhận diện và";
        return $"{ZoneDisplayName(SelectedZoneKind)} {recognition} {lockedRules.Count} mark đang khóa. " +
               "Lý do: " + FormatLockedRuleReasons(lockedRules);
    }

    private async Task RunBusyAsync(string errorCode, Action action)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _lastOperationFailed = false;
            NotifyCommandStates();
            await _revitDispatcher.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            await InvokeOnUiAsync(() => RecordOperationFailure(errorCode, ex));
        }
        finally
        {
            await InvokeOnUiAsync(() => IsBusy = false);
        }

        try
        {
            await InvokeOnUiAsync(UpdateActionGateState);
        }
        catch (Exception ex)
        {
            await InvokeOnUiAsync(() =>
            {
                RecordOperationFailure(errorCode + "_GATE", ex);
                _actionGate = new AbutmentActionGateResult(
                    AbutmentActionGateState.ResolveBlockingIssues,
                    CanCreate: false,
                    CanRebuild: false);
                ActionReadinessText = "Thao tác đã dừng an toàn. Hãy đóng công cụ và gửi file log cho bộ phận phát triển.";
                OnPropertyChanged(nameof(ActionGate));
                SafeNotifyCommandStates();
            });
        }
    }

    private Task InvokeOnUiAsync(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _uiDispatcher.InvokeAsync(action).Task;
    }

    private void RecordOperationFailure(string errorCode, Exception exception)
    {
        AbutmentDiagnostics.LogException(errorCode, exception);
        try
        {
            _lastOperationFailed = true;
            _controller.InvalidateReviewedSnapshot();
            HeaderStatusText = "Thao tác đã dừng an toàn.";
            var retainedIssues = _controller.CurrentPlan?.Issues.ToList()
                                 ?? _controller.Classification?.Issues.ToList()
                                 ?? Issues.ToList();
            retainedIssues.Add(new ValidationIssue(errorCode, ValidationSeverity.Error,
                "Không thể hoàn tất thao tác: " + exception.GetBaseException().Message +
                " Chi tiết đã được ghi vào abutment-rebar.log."));
            UpdateIssues(retainedIssues);
        }
        catch (Exception reportingException)
        {
            AbutmentDiagnostics.LogException(errorCode + "_REPORTING", reportingException);
        }
    }

    private void SafeNotifyCommandStates()
    {
        try
        {
            NotifyCommandStates();
        }
        catch (Exception exception)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_COMMAND_STATE_NOTIFY_FAILED", exception);
        }
    }

    private void NotifyCommandStates()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        RebuildCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void ClearResolvedSelection()
    {
        SelectedZoneDescriptor = null;
        _selectionHostUniqueId = string.Empty;
        _selectionGeometryHash = string.Empty;
        
        _controller.ClearBoundZone();
    }


    private string CurrentZoneStatus() =>
        AvailableZones.FirstOrDefault(item => item.Kind == SelectedZoneKind)?.StatusText
        ?? "Khu vực chưa có dữ liệu phân tích.";

    private static int ZoneSortOrder(AbutmentZoneKind zone) => zone switch
    {
        AbutmentZoneKind.Footing => 0,
        AbutmentZoneKind.Stem => 1,
        AbutmentZoneKind.Backwall => 2,
        AbutmentZoneKind.WingwallLeft => 3,
        AbutmentZoneKind.WingwallRight => 4,
        _ => 10 + (int)zone
    };

    public static string ZoneDisplayName(AbutmentZoneKind zone) => zone switch
    {
        AbutmentZoneKind.Footing => "Móng mố",
        AbutmentZoneKind.Stem => "Thân mố",
        AbutmentZoneKind.Backwall => "Tường đỉnh mố",
        AbutmentZoneKind.WingwallLeft => "Tường cánh trái",
        AbutmentZoneKind.WingwallRight => "Tường cánh phải",
        AbutmentZoneKind.BearingSeat => "Bệ kê gối",
        AbutmentZoneKind.ShearKey => "Khóa chống trượt",
        AbutmentZoneKind.Pedestal => "Bệ gối",
        AbutmentZoneKind.PileHead => "Đầu cọc",
        _ => zone.ToString()
    };
}

public sealed record AbutmentZoneOptionViewModel(
    AbutmentZoneKind Kind,
    string DisplayName,
    string StatusText,
    bool IsRecognized,
    bool HasEnabledRules);

public sealed record AbutmentViewportRenderModeOption(
    AbutmentViewportRenderMode Mode,
    string DisplayName)
{
    public static readonly AbutmentViewportRenderModeOption XRay =
        new(AbutmentViewportRenderMode.XRay, "X-Ray mố");
    public static readonly AbutmentViewportRenderModeOption Wireframe =
        new(AbutmentViewportRenderMode.Wireframe, "Wireframe");
}
