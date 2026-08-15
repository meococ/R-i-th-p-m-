using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Models;

namespace BIM.DatViet.RevitServices;

internal static class RebarApiAdapter
{
    public static Rebar CreateFromCurves(
        Document document,
        RebarBarType barType,
        RebarStyle rebarStyle,
        Element host,
        XYZ normal,
        IList<Curve> curves,
        AbutmentEndGeometryIntent start,
        AbutmentEndGeometryIntent end,
        bool createNewShape = false)
    {
        ValidateEndIntent(start, "đầu");
        ValidateEndIntent(end, "cuối");
#if REVIT2027_OR_GREATER
        if (start.Kind == AbutmentEndGeometryKind.Hook ||
            end.Kind == AbutmentEndGeometryKind.Hook)
            throw new InvalidOperationException(
                "Hook termination cho Revit 2027+ chưa được map sang BarTerminationsData.");
        using var terminations = new BarTerminationsData(document);
        return Rebar.CreateFromCurves(document, rebarStyle, barType, host, normal, curves,
            terminations, true, createNewShape);
#else
        var startHook = ResolveHookType(document, start, "đầu");
        var endHook = ResolveHookType(document, end, "cuối");
        var rebar = Rebar.CreateFromCurves(
            document,
            rebarStyle,
            barType,
            startHook,
            endHook,
            host,
            normal,
            curves,
            HookOrientation(start),
            HookOrientation(end),
            true,
            createNewShape);
        if (start.Kind == AbutmentEndGeometryKind.Hook &&
            Math.Abs(start.HookSpec.RotationDegrees) > 1e-9)
            rebar.SetHookRotationAngle(start.HookSpec.RotationDegrees * Math.PI / 180, 0);
        if (end.Kind == AbutmentEndGeometryKind.Hook &&
            Math.Abs(end.HookSpec.RotationDegrees) > 1e-9)
            rebar.SetHookRotationAngle(end.HookSpec.RotationDegrees * Math.PI / 180, 1);
        return rebar;
#endif
    }

    public static Rebar CreateStraightFreeForm(
        Document document,
        RebarBarType barType,
        Element host,
        IList<Curve> curves)
    {
        if (curves.Count != 1 || curves[0] is not Line)
            throw new InvalidOperationException(
                "CurveDrivenFreeForm hiện chỉ hỗ trợ một đoạn thẳng.");

        var curveSets = new List<IList<Curve>> { curves };
#if REVIT2027_OR_GREATER
        using var result = Rebar.CreateFreeForm(
            document, barType, host, curveSets, RebarStyle.Standard);
        if (result.Error != RebarFreeFormValidationResult.Success || result.Rebar is null)
            throw new InvalidOperationException(
                $"CreateFreeForm thất bại: {result.Error}.");
        var rebar = result.Rebar;
#else
        var rebar = Rebar.CreateFreeForm(
            document, barType, host, curveSets, out var validationResult);
        if (validationResult != RebarFreeFormValidationResult.Success || rebar is null)
            throw new InvalidOperationException(
                $"CreateFreeForm thất bại: {validationResult}.");
#endif
        using var accessor = rebar.GetFreeFormAccessor();
        accessor.WorkshopInstructions = RebarWorkInstructions.Straight;
        return rebar;
    }

    private static void ValidateEndIntent(AbutmentEndGeometryIntent intent, string label)
    {
        if (intent.Kind == AbutmentEndGeometryKind.None) return;
        if (intent.Kind != AbutmentEndGeometryKind.Hook)
            throw new InvalidOperationException(
                $"{label} thanh dùng {intent.Kind}; runtime chỉ hỗ trợ None hoặc Hook.");
        var hook = intent.HookSpec;
        if (string.IsNullOrWhiteSpace(hook.TypeName))
            throw new InvalidOperationException($"{label} thanh thiếu RebarHookType.");
        if (!double.IsFinite(hook.AngleDegrees) || hook.AngleDegrees <= 0 || hook.AngleDegrees > 180)
            throw new InvalidOperationException(
                $"{label} thanh có hook angle ngoài (0, 180]°.");
        if (!double.IsFinite(hook.LegLengthMm) || hook.LegLengthMm <= 0)
            throw new InvalidOperationException(
                $"{label} thanh có hook legLengthMm không hợp lệ.");
        if (!double.IsFinite(hook.BendRadiusMm) || hook.BendRadiusMm <= 0)
            throw new InvalidOperationException(
                $"{label} thanh có hook bendRadiusMm không hợp lệ.");
        if (!hook.Orientation.Equals("Left", StringComparison.OrdinalIgnoreCase) &&
            !hook.Orientation.Equals("Right", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{label} thanh có hook orientation '{hook.Orientation}' không hợp lệ.");
        if (!double.IsFinite(hook.RotationDegrees))
            throw new InvalidOperationException($"{label} thanh có hook rotation không hữu hạn.");
    }

#if !REVIT2027_OR_GREATER
    private static RebarHookType? ResolveHookType(
        Document document,
        AbutmentEndGeometryIntent intent,
        string label)
    {
        if (intent.Kind == AbutmentEndGeometryKind.None) return null;
        var matches = new FilteredElementCollector(document)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .Where(type => type.Name.Equals(intent.HookSpec.TypeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Không tìm thấy RebarHookType '{intent.HookSpec.TypeName}' cho {label} thanh."),
            _ => throw new InvalidOperationException(
                $"RebarHookType '{intent.HookSpec.TypeName}' cho {label} thanh không duy nhất.")
        };
    }

    private static RebarHookOrientation HookOrientation(AbutmentEndGeometryIntent intent) =>
        intent.HookSpec.Orientation.Equals("Right", StringComparison.OrdinalIgnoreCase)
            ? RebarHookOrientation.Right
            : RebarHookOrientation.Left;
#endif

    public static Rebar CreateFromCurves(
        Document document,
        RebarBarType barType,
        Element host,
        XYZ normal,
        IList<Curve> curves,
        bool createNewShape = false) =>
        CreateFromCurves(
            document,
            barType,
            RebarStyle.Standard,
            host,
            normal,
            curves,
            new AbutmentEndGeometryIntent { Kind = AbutmentEndGeometryKind.None },
            new AbutmentEndGeometryIntent { Kind = AbutmentEndGeometryKind.None },
            createNewShape);
}
