using System.Globalization;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.RevitServices;
using Nice3point.Revit.Toolkit.External;

namespace BIM.DatViet.Commands;

/// <summary>
/// Creates the bar bending schedule of one abutment from the bars in the model.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public sealed class AbutmentRebarScheduleCommand : ExternalCommand
{
    private const string Title = "Bảng Thống Kê Thép Mố";

    public override void Execute()
    {
        var uiDocument = Application.ActiveUIDocument;
        var document = uiDocument.Document;
        try
        {
            var host = AbutmentSchedulePicker.PickAbutmentHost(uiDocument);
            if (!AbutmentScheduleService.TryReadManagedBars(document, host, out var source, out var readError))
            {
                TaskDialog.Show(Title, readError);
                return;
            }
            if (!AbutmentRebarScheduleKernel.TryBuildFinishedBarSchedule(
                    source.Rows, out var lines, out var buildError))
            {
                TaskDialog.Show(Title, buildError);
                return;
            }

            IReadOnlyList<string> notes;
            ViewSchedule schedule;
            using (var transaction = new Transaction(document, "DVB tạo bảng thống kê thép mố"))
            {
                RevitTransactionGuard.Start(transaction, "DVB tạo bảng thống kê thép mố");
                schedule = AbutmentScheduleService.CreateFinishedBarSchedule(document, source, out notes);
                RevitTransactionGuard.Commit(transaction, "DVB tạo bảng thống kê thép mố");
            }

            uiDocument.ActiveView = schedule;
            TaskDialog.Show(Title, AbutmentSchedulePicker.Describe(source, lines, schedule.Name, notes));
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // User cancelled selection; model is unchanged.
        }
        catch (Exception exception)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_SCHEDULE_COMMAND_FAILED", exception);
            TaskDialog.Show(Title,
                "Không tạo được bảng thống kê: " + exception.GetBaseException().Message +
                "\nChi tiết: %LocalAppData%\\BIM-DatViet\\Logs\\abutment-rebar.log");
        }
    }
}

/// <summary>
/// Exports the yard cutting list of one abutment. Separate from the schedule because the yard cuts
/// pieces while the drawing dimensions finished bars, and one table cannot honestly be both.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.ReadOnly)]
public sealed class AbutmentCuttingListCommand : ExternalCommand
{
    private const string Title = "Bảng Cắt Phôi Thép Mố";

    public override void Execute()
    {
        var uiDocument = Application.ActiveUIDocument;
        try
        {
            var host = AbutmentSchedulePicker.PickAbutmentHost(uiDocument);
            if (!AbutmentScheduleService.TryReadManagedBars(
                    uiDocument.Document, host, out var source, out var readError))
            {
                TaskDialog.Show(Title, readError);
                return;
            }
            if (!AbutmentRebarScheduleKernel.TryBuildFinishedBarSchedule(
                    source.Rows, out var scheduleLines, out var scheduleError))
            {
                TaskDialog.Show(Title, scheduleError);
                return;
            }
            if (!AbutmentRebarScheduleKernel.TryBuildCuttingList(
                    source.Rows, out var cutLines, out var cutError))
            {
                TaskDialog.Show(Title, cutError);
                return;
            }

            var path = AbutmentScheduleService.ExportCuttingList(source, cutLines, scheduleLines);
            var text = new StringBuilder();
            text.AppendLine($"Đã xuất bảng cắt phôi cho bộ thép {source.AssemblyId}.");
            text.AppendLine();
            foreach (var line in cutLines)
                text.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "{0} (bản vẽ {1}) Ø{2:0.#}: {3} đoạn × {4:0.#} mm = {5:0.#} kg",
                    line.InternalMark, line.DrawingMark, line.BarDiameterMm, line.PieceCount,
                    line.CutLengthMm, line.MassKg));
            text.AppendLine();
            text.AppendLine(string.Format(CultureInfo.CurrentCulture,
                "Tổng {0} đoạn, {1:0.#} kg.",
                cutLines.Sum(line => line.PieceCount), cutLines.Sum(line => line.MassKg)));
            text.AppendLine();
            text.AppendLine(path);
            TaskDialog.Show(Title, text.ToString());
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // User cancelled selection; nothing was written.
        }
        catch (Exception exception)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_CUTTING_LIST_COMMAND_FAILED", exception);
            TaskDialog.Show(Title,
                "Không xuất được bảng cắt phôi: " + exception.GetBaseException().Message +
                "\nChi tiết: %LocalAppData%\\BIM-DatViet\\Logs\\abutment-rebar.log");
        }
    }
}

internal static class AbutmentSchedulePicker
{
    public static Element PickAbutmentHost(UIDocument uiDocument)
    {
        var reference = uiDocument.Selection.PickObject(
            ObjectType.Element,
            new AbutmentSelectionFilter(),
            "Chọn mố cầu đã rải thép để lập bảng.");
        return uiDocument.Document.GetElement(reference)
               ?? throw new InvalidOperationException("Không đọc được host đã chọn.");
    }

    public static string Describe(
        AbutmentScheduleSource source,
        IReadOnlyList<AbutmentScheduleLine> lines,
        string scheduleName,
        IReadOnlyList<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine($"Đã tạo bảng '{scheduleName}'.");
        text.AppendLine();
        text.AppendLine(string.Format(CultureInfo.CurrentCulture,
            "{0} số hiệu, {1} thanh hoàn thiện từ {2} đối tượng thép trong model.",
            lines.Count, lines.Sum(line => line.Quantity), source.Rows.Count));
        text.AppendLine();
        foreach (var line in lines)
            text.AppendLine(string.Format(CultureInfo.CurrentCulture,
                "{0} (bản vẽ {1}) Ø{2:0.#}: {3} thanh × {4:0.#} mm, {5} mối nối × {6:0.#} mm, {7:0.#} kg",
                line.InternalMark, line.DrawingMark, line.BarDiameterMm, line.Quantity,
                line.BarLengthMm, line.SpliceCountPerBar, line.LapLengthMm, line.MassKg));
        text.AppendLine();
        text.AppendLine(string.Format(CultureInfo.CurrentCulture,
            "Tổng khối lượng {0:0.#} kg.", lines.Sum(line => line.MassKg)));
        if (notes.Count == 0) return text.ToString();
        text.AppendLine();
        text.AppendLine("Lưu ý:");
        foreach (var note in notes) text.AppendLine("- " + note);
        return text.ToString();
    }
}
