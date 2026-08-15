using Autodesk.Revit.DB;

namespace BIM.DatViet.Infrastructure;

/// <summary>
/// Cross-version ElementId helpers. Kept as ordinary static methods (not extensions)
/// so they never collide with Nice3point.Revit.Extensions ElementId.ToLong().
/// </summary>
internal static class ElementIdCompat
{
    public static ElementId FromLong(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }

    public static long ToLong(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }
}
