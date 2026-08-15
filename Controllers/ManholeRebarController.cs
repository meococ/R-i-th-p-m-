using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using BIM.DatViet.Planning;
using BIM.DatViet.Preview;
using BIM.DatViet.Recognition;
using BIM.DatViet.RevitServices;

namespace BIM.DatViet.Controllers;

public sealed class ManholeRebarController
{
    private readonly UIDocument _uiDocument;
    private readonly Document _document;
    private readonly IReadOnlyList<Element> _selected;
    private readonly Element _seed;
    private readonly RebarTypeCatalog _catalog;
    private ManholeAssembly? _assembly;

    public ManholeRebarController(UIDocument uiDocument, IReadOnlyList<Element> selected)
    {
        if (selected is null || selected.Count == 0)
            throw new ArgumentException("Cần ít nhất một element đã chọn.", nameof(selected));

        _uiDocument = uiDocument;
        _document = uiDocument.Document;
        _selected = selected
            .GroupBy(element => element.Id)
            .Select(group => group.First())
            .ToArray();
        _seed = _selected[0];
        _catalog = new RebarTypeCatalog(_document);
        var recognition = new ManholeRecognizer(_document).Resolve(_selected);
        _assembly = recognition.Assembly;
        LastRecognitionIssues = recognition.Issues;
    }

    public IReadOnlyList<string> BarTypeNames => _catalog.Names;
    public IReadOnlyList<Element> SelectedElements => _selected;
    public RebarPlan? CurrentPlan { get; private set; }

    public ManholeRebarPreset LoadPreset() => ManholeSettingsStore.Load(_document, ResolveSettingsHost()).Preset;

    public RebarPlan? Analyze(ManholeRebarPreset preset)
    {
        var recognition = new ManholeRecognizer(_document).Resolve(_selected);
        if (recognition.Assembly is null)
        {
            CurrentPlan = null;
            RebarPreviewServer.Clear();
            _uiDocument.RefreshActiveView();
            LastRecognitionIssues = recognition.Issues;
            return null;
        }
        _assembly = recognition.Assembly;
        var settings = ManholeSettingsStore.Load(_document, ResolveSettingsHost());
        var saved = new XYZ(settings.W1X, settings.W1Y, settings.W1Z);
        if (saved.GetLength() > 0.5)
            ManholeRecognizer.SetWallOne(_assembly,
                _assembly.Walls.OrderByDescending(wall => wall.Outward.DotProduct(saved.Normalize())).First());
        return BuildFromCurrent(preset, recognition.Issues);
    }

    public RebarPlan? RotateWallOne(ManholeRebarPreset preset)
    {
        if (_assembly is null) return Analyze(preset);
        ManholeRecognizer.RotateWallNumbers(_assembly);
        var w1 = _assembly.Walls.Single(wall => wall.Number == 1);
        ManholeSettingsStore.Save(_document, ResolveSettingsHost(), preset, w1.Outward);
        return BuildFromCurrent(preset, LastRecognitionIssues);
    }

    public CreateReceipt Create(ManholeRebarPreset preset)
    {
        var plan = BuildFromCurrent(preset, LastRecognitionIssues);
        if (plan is null)
        {
            var failed = new CreateReceipt();
            failed.Issues.Add(new ValidationIssue("NO_PLAN", ValidationSeverity.Error, "Chưa có RebarPlan hợp lệ."));
            return failed;
        }
        if (_assembly is not null)
        {
            var w1 = _assembly.Walls.Single(wall => wall.Number == 1);
            ManholeSettingsStore.Save(_document, ResolveSettingsHost(), preset, w1.Outward);
        }
        var receipt = new RebarFactory(_document).CreateOrUpdate(plan);
        if (receipt.Succeeded)
        {
            RebarPreviewServer.Clear();
            _uiDocument.RefreshActiveView();
        }
        return receipt;
    }

    /// <summary>Prefer stable assembly primary (bottom slab / single host) over the original pick.</summary>
    private Element ResolveSettingsHost()
    {
        if (_assembly is not null && _document.GetElement(_assembly.PrimaryHostId) is { } primary)
            return primary;
        return _seed;
    }

    public void ClearPreview()
    {
        RebarPreviewServer.Clear();
        _uiDocument.RefreshActiveView();
    }

    public IReadOnlyList<ValidationIssue> LastRecognitionIssues { get; private set; } = [];

    private RebarPlan? BuildFromCurrent(ManholeRebarPreset preset, IReadOnlyCollection<ValidationIssue> recognitionIssues)
    {
        if (_assembly is null) return null;
        LastRecognitionIssues = recognitionIssues.ToArray();
        _assembly.Openings.Clear();
        var presetIssues = ManholeRebarPlanner.ValidatePreset(preset);
        if (presetIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            var invalidPlan = new ManholeRebarPlanner().Build(_assembly, preset, _catalog);
            invalidPlan.Issues.InsertRange(0, recognitionIssues);
            CurrentPlan = invalidPlan;
            RebarPreviewServer.Clear();
            _uiDocument.RefreshActiveView();
            return invalidPlan;
        }
        var radius = ResolveMaximumRadius(preset);
        var expansion = UnitUtils.ConvertToInternalUnits(
            preset.OpeningClearanceMm + preset.CoverMm + 1, UnitTypeId.Millimeters) + radius;
        _assembly.Openings.AddRange(new OpeningDetector(_document).Detect(_assembly, expansion));
        var plan = new ManholeRebarPlanner().Build(_assembly, preset, _catalog);
        plan.Issues.InsertRange(0, recognitionIssues);
        plan.Issues.AddRange(OpeningEnvelopeSolidValidator.Validate(plan));
        CurrentPlan = plan;
        RebarPreviewServer.Show(plan);
        _uiDocument.RefreshActiveView();
        return plan;
    }

    private double ResolveMaximumRadius(ManholeRebarPreset preset)
    {
        var radii = new[] { preset.SlabBarTypeName, preset.WallVerticalBarTypeName,
                preset.WallHorizontalBarTypeName, preset.OpeningBarTypeName }
            .Select(name => _catalog.TryGet(name, out var type) ? type.Diameter / 2 : 0);
        return radii.Max();
    }
}
