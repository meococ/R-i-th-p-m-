using System.Windows.Media;
using BIM.DatViet.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaColor = System.Windows.Media.Color;

namespace BIM.DatViet.ViewModels;

public sealed class RebarLayerItemViewModel : ObservableObject
{
    private readonly Func<string, double>? _diameterResolver;
    private bool _isEnabled = true;
    private string _barTypeName = "D20";
    private double _spacingMm = 150;
    private double? _coverOverrideMm;
    private string _plannedQuantityText = "—";
    private string _editErrorText = string.Empty;

    public RebarLayerItemViewModel(
        AbutmentZoneRule rule,
        Func<string, double>? diameterResolver = null)
    {
        _diameterResolver = diameterResolver;
        OriginalRule = rule;
        RuleId = rule.RuleId;
        CanonicalMark = rule.CanonicalMark;
        SourceDrawingMark = rule.SourceDrawingMark;
        _barTypeName = rule.BarTypeName;
        DiameterMm = rule.DiameterMm;
        _spacingMm = rule.SpacingMm;
        _coverOverrideMm = rule.CoverOverrideMm;
        _isEnabled = rule.Enabled;
        IsSupported = rule.Enabled || string.IsNullOrWhiteSpace(rule.DisabledReason);
        DisabledReason = rule.DisabledReason;
        InitLayerMetadata(rule);
    }

    public AbutmentZoneRule OriginalRule { get; }
    public string RuleId { get; }
    public string CanonicalMark { get; }
    public string SourceDrawingMark { get; }
    public string MarkDisplayText => $"Canonical {CanonicalMark} ← Source {SourceDrawingMark}";
    public string LayerName { get; private set; } = string.Empty;
    public string FaceRoleText { get; private set; } = string.Empty;
    public Brush LayerColor { get; private set; } = Brushes.Cyan;
    public bool IsSupported { get; }
    public string DisabledReason { get; }
    public bool CanEdit => IsSupported && IsEnabled;
    public string ToggleAutomationName => $"Trạng thái lớp thép {CanonicalMark}, nguồn {SourceDrawingMark}, khóa theo preset";
    public string BarTypeAutomationName => $"Loại thanh thép của {CanonicalMark}";
    public string SpacingAutomationName => $"Khoảng cách thanh thép {CanonicalMark}, milimét";
    public string DesignBasisText
    {
        get
        {
            var cover = OriginalRule.FootingCover.FaceMm is > 0
                ? $"cover mặt {OriginalRule.FootingCover.FaceMm:0.#} mm"
                : "cover chưa khóa";
            var grade = string.IsNullOrWhiteSpace(OriginalRule.RequiredBarGrade)
                ? "mác thép chưa khóa"
                : $"{OriginalRule.RequiredBarGrade}, fy ≥ {OriginalRule.MinimumYieldStrengthMpa:0.#} MPa";
            var station = OriginalRule.StationSchedule.ExpectedBarCount > 0
                ? $"{OriginalRule.StationSchedule.ExpectedBarCount} station theo hồ sơ"
                : "station chưa khóa";
            var source = OriginalRule.SourceLocator.Page > 0
                ? $"P{OriginalRule.SourceLocator.Page} {OriginalRule.SourceLocator.Section}".Trim()
                : "nguồn chưa định vị";
            return $"{OriginalRule.SourceResolutionStatus} • {source} • {grade} • {station} • {cover}";
        }
    }

    public string DistributionText =>
        OriginalRule.StationSchedule.ExpectedBarCount > 0 &&
        OriginalRule.StationSchedule.CenterlineOffsetsMm.Count ==
        OriginalRule.StationSchedule.ExpectedBarCount
            ? $"{OriginalRule.StationSchedule.ExpectedBarCount} vị trí khóa"
            : OriginalRule.StationSchedule.ExpectedBarCount > 0
                ? $"n={OriginalRule.StationSchedule.ExpectedBarCount}; st chưa khóa"
                : "matrix only";

    public bool IsEnabled => _isEnabled;

    public string BarTypeName
    {
        get => _barTypeName;
        set
        {
            if (string.Equals(_barTypeName, value, StringComparison.Ordinal))
            {
                return;
            }
            double? resolvedDiameter = null;
            if (_diameterResolver is not null && !string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    resolvedDiameter = _diameterResolver(value);
                }
                catch (Exception ex)
                {
                    _editErrorText = "Không đọc được đường kính loại thép: " + ex.Message;
                    OnPropertyChanged(nameof(StatusText));
                    return;
                }
            }
            if (SetProperty(ref _barTypeName, value))
            {
                _editErrorText = string.Empty;
                if (resolvedDiameter is not null)
                {
                    DiameterMm = resolvedDiameter.Value;
                    OnPropertyChanged(nameof(DiameterMm));
                    OnPropertyChanged(nameof(DesignBasisText));
                }
                OnPropertyChanged(nameof(StatusText));
                OnChanged?.Invoke();
            }
        }
    }

    public double SpacingMm => _spacingMm;

    public double? CoverOverrideMm => _coverOverrideMm;

    public double DiameterMm { get; private set; }

    public string PlannedQuantityText
    {
        get => _plannedQuantityText;
        set => SetProperty(ref _plannedQuantityText, value);
    }

    public string StatusText => !string.IsNullOrWhiteSpace(_editErrorText)
        ? _editErrorText
        : !IsEnabled
            ? string.IsNullOrWhiteSpace(DisabledReason)
                ? "Mark khóa: chưa ghi lý do khóa."
                : "Mark khóa: " + DisabledReason
            : "Mark đang mở cho Create";

    public Action? OnChanged { get; set; }

    private void InitLayerMetadata(AbutmentZoneRule rule)
    {
        (LayerName, FaceRoleText, LayerColor) = rule.CanonicalMark.ToUpperInvariant() switch
        {
            "A1" => ("Bệ mố • lớp trên • phương dọc", "Đỉnh • phương dọc", CreateFrozenBrush("#E91E63")),
            "A2" => ("Bệ mố • lớp dưới • phương dọc", "Đáy • phương dọc", CreateFrozenBrush("#00BCD4")),
            "A3-T" => ("Bệ mố • lớp trên • phương ngang", "Đỉnh • phương ngang", CreateFrozenBrush("#4CAF50")),
            "A3-B" => ("Bệ mố • lớp dưới • phương ngang", "Đáy • phương ngang", CreateFrozenBrush("#FF9800")),
            "A4" => ("Bệ mố • thép biên/perimeter", "Biên • path chưa khóa", CreateFrozenBrush("#795548")),
            "A6" => ("Bệ mố • thép đứng xuyên bề dày", "Đứng • path chưa khóa", CreateFrozenBrush("#9C27B0")),
            _ when rule.CanonicalMark.StartsWith("B", StringComparison.OrdinalIgnoreCase) =>
                ("Thân/tường đỉnh mố • " + rule.SourceDrawingMark, rule.FaceRole.ToString(),
                    CreateFrozenBrush("#2196F3")),
            _ when rule.CanonicalMark.StartsWith("C", StringComparison.OrdinalIgnoreCase) =>
                ("Tường cánh • " + rule.SourceDrawingMark, rule.FaceRole.ToString(),
                    CreateFrozenBrush("#009688")),
            _ when rule.CanonicalMark.StartsWith("F", StringComparison.OrdinalIgnoreCase) =>
                ("Hook/tie/thép đặc biệt • " + rule.SourceDrawingMark, rule.FaceRole.ToString(),
                    CreateFrozenBrush("#FFC107")),
            _ => (rule.CanonicalMark, rule.FaceRole.ToString(), CreateFrozenBrush("#607D8B"))
        };
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var color = (MediaColor)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }


    public AbutmentRuleEdit ToEdit() => new()
    {
        RuleId = RuleId,
        Enabled = IsEnabled,
        BarTypeName = BarTypeName,
        DiameterMm = DiameterMm,
        SpacingMm = SpacingMm,
        FixedNumber = OriginalRule.FixedNumber,
        CoverOverrideMm = CoverOverrideMm,
        LayerOrder = OriginalRule.LayerOrder,
        ClearGapToPreviousMm = OriginalRule.ClearGapToPreviousMm,
        StartStationClearanceMm = OriginalRule.StartStationClearanceMm,
        EndStationClearanceMm = OriginalRule.EndStationClearanceMm
    };
}
