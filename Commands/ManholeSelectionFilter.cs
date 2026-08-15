using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using BIM.DatViet.Infrastructure;

namespace BIM.DatViet.Commands;

internal sealed class ManholeSelectionFilter : ISelectionFilter
{
    /// <summary>
    /// Categories allowed for manhole rebar workflow. Structural Foundation is a first-class
    /// slab host (bottom/top), not a fallback.
    /// </summary>
    private static readonly HashSet<long> AllowedCategories =
    [
        (long)BuiltInCategory.OST_GenericModel,
        (long)BuiltInCategory.OST_StructuralFoundation,
        (long)BuiltInCategory.OST_Floors,
        (long)BuiltInCategory.OST_Walls,
        (long)BuiltInCategory.OST_Parts
    ];

    public bool AllowElement(Element element) =>
        element.Category is not null && AllowedCategories.Contains(ElementIdCompat.ToLong(element.Category.Id));

    public bool AllowReference(Reference reference, XYZ position) => true;
}
