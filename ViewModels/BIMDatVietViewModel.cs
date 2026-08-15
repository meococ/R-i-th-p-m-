using System.Collections.ObjectModel;
using BIM.DatViet.Controllers;
using BIM.DatViet.Models;

namespace BIM.DatViet.ViewModels;

public sealed class BIMDatVietViewModel : ObservableObject
{
    private readonly ManholeRebarController _controller;
    private string _summary = "Chưa phân tích.";
    private string _result = string.Empty;
    private bool _canCreate;

    public BIMDatVietViewModel(ManholeRebarController controller)
    {
        _controller = controller;
        Preset = controller.LoadPreset();
        BarTypeNames = controller.BarTypeNames;
        NormalizeTypeSelections();
    }

    public ManholeRebarPreset Preset { get; }
    public IReadOnlyList<string> BarTypeNames { get; }
    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public bool CanCreate
    {
        get => _canCreate;
        private set => SetProperty(ref _canCreate, value);
    }

    public void Analyze() => ApplyPlan(_controller.Analyze(Preset));

    public void RotateWallOne() => ApplyPlan(_controller.RotateWallOne(Preset));

    public void Create()
    {
        var receipt = _controller.Create(Preset);
        Issues.Clear();
        foreach (var issue in receipt.Issues) Issues.Add(issue);
        var rollbackUnconfirmed = receipt.Issues.Any(issue =>
            issue.Code == "CREATE_ROLLBACK_UNCONFIRMED");
        Result = receipt.Succeeded
            ? $"Hoàn tất run {receipt.RunId}: tạo {receipt.CreatedIds.Count}, thay thế {receipt.RemovedIds.Count} Rebar cũ của tool. Model chưa được save."
            : rollbackUnconfirmed
                ? "Không tạo được thép và Revit chưa xác nhận rollback. Dừng thao tác; kiểm tra model trước khi tiếp tục."
                : "Không tạo thép. TransactionGroup đã rollback và giữ nguyên kết quả cũ.";
        CanCreate = !receipt.Succeeded && _controller.CurrentPlan?.CanCreate == true;
    }

    public void ClearPreview() => _controller.ClearPreview();

    private void ApplyPlan(RebarPlan? plan)
    {
        Issues.Clear();
        Result = string.Empty;
        if (plan is null)
        {
            foreach (var issue in _controller.LastRecognitionIssues) Issues.Add(issue);
            Summary = "Không nhận diện được một cụm hố ga hợp lệ.";
            CanCreate = false;
            return;
        }
        foreach (var issue in plan.Issues) Issues.Add(issue);
        var assembly = plan.Assembly;
        Summary = $"{assembly.ManholeKey} | {(assembly.IsSingleHost ? "1 family host" : "6 host")} | " +
                  $"{plan.Bars.Count} Rebar element / {plan.PlannedBarCount} vị trí thanh | " +
                  $"{assembly.Openings.Count} opening | W1 host {assembly.Walls.Single(w => w.Number == 1).HostId}";
        CanCreate = plan.CanCreate;
    }

    private void NormalizeTypeSelections()
    {
        if (BarTypeNames.Count == 0) return;
        string Resolve(string current) => BarTypeNames.FirstOrDefault(name =>
            name.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? BarTypeNames[0];
        Preset.SlabBarTypeName = Resolve(Preset.SlabBarTypeName);
        Preset.WallVerticalBarTypeName = Resolve(Preset.WallVerticalBarTypeName);
        Preset.WallHorizontalBarTypeName = Resolve(Preset.WallHorizontalBarTypeName);
        Preset.OpeningBarTypeName = Resolve(Preset.OpeningBarTypeName);
    }
}
