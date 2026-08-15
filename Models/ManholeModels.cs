using Autodesk.Revit.DB;

namespace BIM.DatViet.Models;

public enum ManholeComponentKind
{
    Bottom,
    Wall,
    Top
}

public enum RebarFace
{
    None,
    Inner,
    Outer,
    Top,
    Bottom
}

public enum RebarDirection
{
    U,
    V,
    Horizontal,
    Vertical,
    Corner,
    Opening
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public sealed class ValidationIssue
{
    public ValidationIssue(string code, ValidationSeverity severity, string message, params ElementId[] elementIds)
    {
        Code = code;
        Severity = severity;
        Message = message;
        ElementIds = elementIds.Where(id => id != ElementId.InvalidElementId).ToArray();
    }

    public string Code { get; }
    public ValidationSeverity Severity { get; }
    public string Message { get; }
    public IReadOnlyList<ElementId> ElementIds { get; }
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}

public sealed class LocalFrame
{
    public LocalFrame(XYZ origin, XYZ u, XYZ v)
    {
        Origin = origin;
        U = u.Normalize();
        V = v.Normalize();
        Z = XYZ.BasisZ;
    }

    public XYZ Origin { get; }
    public XYZ U { get; }
    public XYZ V { get; }
    public XYZ Z { get; }

    public double ToU(XYZ point) => (point - Origin).DotProduct(U);
    public double ToV(XYZ point) => (point - Origin).DotProduct(V);
    public double ToZ(XYZ point) => point.Z;
    public XYZ Of(double u, double v, double z) => Origin + U * u + V * v + XYZ.BasisZ * (z - Origin.Z);
}

public sealed class ManholeComponent
{
    public required ManholeComponentKind Kind { get; init; }
    public required ElementId HostId { get; init; }
    public string Key { get; init; } = string.Empty;
    public BoundingBoxXYZ? BoundingBox { get; init; }
    public double MinU { get; init; }
    public double MaxU { get; init; }
    public double MinV { get; init; }
    public double MaxV { get; init; }
}

public sealed class WallDescriptor
{
    public required ElementId HostId { get; init; }
    public required XYZ Center { get; init; }
    public required XYZ Outward { get; init; }
    public required XYZ Tangent { get; init; }
    public required double Thickness { get; init; }
    public required double MinAlong { get; init; }
    public required double MaxAlong { get; init; }
    public double MinZ { get; init; }
    public double MaxZ { get; init; }
    public int Number { get; set; }
    public string Key => $"W{Number}";
}

public sealed class OpeningEnvelope
{
    public required string Key { get; init; }
    public required ElementId SourceId { get; init; }
    public required int WallNumber { get; init; }
    public ManholeComponentKind Component { get; init; } = ManholeComponentKind.Wall;
    public required double MinAlong { get; init; }
    public required double MaxAlong { get; init; }
    public required double MinZ { get; init; }
    public required double MaxZ { get; init; }
    public double MinU { get; init; }
    public double MaxU { get; init; }
    public double MinV { get; init; }
    public double MaxV { get; init; }
    public bool IsVirtual { get; init; }
}

public sealed class ManholeAssembly
{
    public required ElementId PrimaryHostId { get; init; }
    public required string ManholeKey { get; set; }
    public required LocalFrame Frame { get; init; }
    public required ManholeComponent Bottom { get; init; }
    public required ManholeComponent Top { get; init; }
    public required IReadOnlyList<WallDescriptor> Walls { get; init; }
    public required IReadOnlyList<ElementId> SourceElementIds { get; init; }
    public List<OpeningEnvelope> Openings { get; } = [];
    public double MinU { get; init; }
    public double MaxU { get; init; }
    public double MinV { get; init; }
    public double MaxV { get; init; }
    public double BottomZ { get; init; }
    public double BottomSlabTopZ { get; init; }
    public double TopSlabBottomZ { get; init; }
    public double TopZ { get; init; }
    public bool IsSingleHost { get; init; }
}

public sealed class PlannedBar
{
    public required string Key { get; init; }
    public required ElementId HostId { get; init; }
    public required string BarTypeName { get; init; }
    public required string Partition { get; init; }
    public required ManholeComponentKind Component { get; init; }
    public required RebarFace Face { get; init; }
    public required RebarDirection Direction { get; init; }
    public required IReadOnlyList<XYZ> Points { get; init; }
    public required XYZ PlaneNormal { get; init; }
    public int WallNumber { get; init; }
    public bool IsOpeningBar { get; init; }
    public bool IsSet { get; init; }
    public double SetLength { get; init; }
    public double MaxSpacing { get; init; }
    public int PlannedQuantity { get; init; } = 1;
}

public sealed class RebarPlan
{
    public required ManholeAssembly Assembly { get; init; }
    public required string SpecHash { get; init; }
    public List<PlannedBar> Bars { get; } = [];
    public List<ValidationIssue> Issues { get; } = [];
    public bool CanCreate => Issues.All(issue => issue.Severity != ValidationSeverity.Error);
    public int PlannedBarCount => Bars.Sum(bar => Math.Max(1, bar.PlannedQuantity));
}

public sealed class CreateReceipt
{
    public List<ElementId> CreatedIds { get; } = [];
    public List<ElementId> UpdatedIds { get; } = [];
    public List<ElementId> RemovedIds { get; } = [];
    public List<ValidationIssue> Issues { get; } = [];
    public bool Succeeded { get; set; }
    public string RunId { get; set; } = string.Empty;
}
