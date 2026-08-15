using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIM.DatViet.Controllers;
using BIM.DatViet.Domain;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using BIM.DatViet.Views;
using Nice3point.Revit.Toolkit.External;

namespace BIM.DatViet.Commands;

[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public sealed class AbutmentRebarCommand : ExternalCommand
{
    public override void Execute()
    {
        var uiDocument = Application.ActiveUIDocument;
        try
        {
            var host = ResolveHost(uiDocument);
            uiDocument.Selection.SetElementIds(Array.Empty<ElementId>());

            var controller = new AbutmentRebarController(uiDocument, host);
            var dispatcher = new AbutmentRevitExternalEventDispatcher();
            var view = new AbutmentRebarView(controller, dispatcher);
            new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
            view.Closed += (_, _) => dispatcher.Dispose();
            view.Show();
            view.PrepareInitialPreview();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // User cancelled selection; model is unchanged.
        }
        catch (Exception exception)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_COMMAND_OPEN_FAILED", exception);
            // Last-resort visible feedback when even the failure UI cannot be constructed.
            try
            {
                TaskDialog.Show(
                    "Rải Thép Mố Cầu",
                    "Không mở được công cụ: " + exception.GetBaseException().Message +
                    "\nChi tiết: %LocalAppData%\\BIM-DatViet\\Logs\\abutment-rebar.log");
            }
            catch
            {
                // Never let diagnostics/UI secondary failures crash Revit command unwind.
            }
        }
    }

    private static Element ResolveHost(UIDocument uiDocument)
    {
        var document = uiDocument.Document;
#if DEBUG
        // Debug-only shortcut so a diagnostic run can target a known host without a click.
        // It still has to clear AbutmentSelectionFilter: an ElementId that is not a rebar-hosting
        // Generic Model would otherwise reach the create path, which AGENT.md §6 forbids. Release
        // builds compile this block away entirely.
        var diagnosticHostId = Environment.GetEnvironmentVariable("BIM_DATVIET_DIAGNOSTIC_HOST_ID");
        if (long.TryParse(diagnosticHostId, out var idValue))
        {
            var diagnosticHost = document.GetElement(new ElementId(idValue));
            if (diagnosticHost is null)
                throw new InvalidOperationException(
                    $"BIM_DATVIET_DIAGNOSTIC_HOST_ID={idValue} không tồn tại trong document " +
                    $"'{document.Title}'. Gỡ biến môi trường hoặc trỏ đúng id.");
            if (!new AbutmentSelectionFilter().AllowElement(diagnosticHost))
                throw new InvalidOperationException(
                    $"BIM_DATVIET_DIAGNOSTIC_HOST_ID={idValue} trỏ tới " +
                    $"'{diagnosticHost.Category?.Name ?? "không có category"}' chứ không phải một " +
                    $"Generic Model đã bật rebar host; tạo thép trên đó sẽ hỏng cấu kiện khác. " +
                    $"Gỡ biến môi trường hoặc trỏ đúng id.");

            AbutmentDiagnostics.LogMessage(
                "ABUTMENT_DIAGNOSTIC_HOST_OVERRIDE",
                $"Bỏ qua PickObject vì BIM_DATVIET_DIAGNOSTIC_HOST_ID={idValue}. " +
                $"Host: {diagnosticHost.Name} ({diagnosticHost.UniqueId}) trong '{document.Title}'.");
            return diagnosticHost;
        }
#endif

        // Nhận selection có sẵn trước khi hỏi. Hai lý do:
        //  1. Đây là thói quen Revit chuẩn — chọn cấu kiện rồi bấm nút.
        //  2. PickObject bắt buộc phải có cú click vào hình trong viewport, nên bản Release trước đây
        //     không thể do agent điều khiển. Chọn sẵn bằng "Select by ID" rồi bấm nút thì chạy được.
        // Nhiều mố hợp lệ cùng lúc vẫn phải hỏi: đoán bừa là rải thép nhầm mố.
        var filter = new AbutmentSelectionFilter();
        var preselected = uiDocument.Selection.GetElementIds()
            .Select(document.GetElement)
            .Where(element => element is not null && filter.AllowElement(element))
            .ToList();

        var decision = AbutmentHostSelectionKernel.Decide(preselected.Count);
        AbutmentDiagnostics.LogMessage(
            "ABUTMENT_HOST_SELECTION",
            $"{decision.Outcome}: {decision.Reason}");
        if (!decision.ShouldPrompt) return preselected[0];

        var reference = uiDocument.Selection.PickObject(ObjectType.Element, filter,
            "Chọn đúng một Generic Model mố cầu đã bật rebar host.");
        return document.GetElement(reference)
               ?? throw new InvalidOperationException("Không đọc được host đã chọn.");
    }

}
