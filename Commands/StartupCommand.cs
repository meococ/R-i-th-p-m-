using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIM.DatViet.Controllers;
using BIM.DatViet.ViewModels;
using BIM.DatViet.Views;
using Nice3point.Revit.Toolkit.External;

namespace BIM.DatViet.Commands;

[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public sealed class StartupCommand : ExternalCommand
{
    public override void Execute()
    {
        var uiDocument = Application.ActiveUIDocument;
        ManholeRebarController? controller = null;
        try
        {
            // Multi-select: 1 element (auto-gom cụm) hoặc chọn đủ 4 Wall + 2 Foundation/Floor/Part.
            var references = uiDocument.Selection.PickObjects(
                ObjectType.Element,
                new ManholeSelectionFilter(),
                "Chọn 1 hoặc nhiều: Wall / Structural Foundation / Floor / Part / Generic Model thuộc hố ga. " +
                "Gợi ý multi-host: 4 tường + đáy + nắp (Foundation/Floor). Finish để phân tích.");
            if (references is null || references.Count == 0)
                throw new InvalidOperationException("Chưa chọn element nào.");

            var selected = references
                .Select(reference => uiDocument.Document.GetElement(reference))
                .Where(element => element is not null)
                .Cast<Element>()
                .GroupBy(element => element.Id)
                .Select(group => group.First())
                .ToList();
            if (selected.Count == 0)
                throw new InvalidOperationException("Không đọc được đối tượng đã chọn.");

            controller = new ManholeRebarController(uiDocument, selected);
            var viewModel = new BIMDatVietViewModel(controller);
            viewModel.Analyze();
            var view = new BIMDatVietView(viewModel);
            view.ShowDialog();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // User cancelled the pick; leave model unchanged.
        }
        catch (Exception exception)
        {
            TaskDialog.Show("Rải Thép Hố Ga", $"Không thể mở workflow:\n{exception.Message}");
        }
        finally
        {
            controller?.ClearPreview();
        }
    }
}
