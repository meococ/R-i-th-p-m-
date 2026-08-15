using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;

namespace BIM.DatViet.Commands;

public sealed class AbutmentSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element element) =>
        element.Category?.Id == new ElementId(BuiltInCategory.OST_GenericModel) &&
        RebarHostData.IsValidHost(element);
    public bool AllowReference(Reference reference, XYZ position) => false;
}
