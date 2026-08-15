using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using BIM.DatViet.Models;
using BIM.DatViet.Planning;

namespace BIM.DatViet.Preview;

/// <summary>
/// Read-only transient preview. No Revit element is created by Analyze/Preview.
/// </summary>
public sealed class RebarPreviewServer : IDirectContext3DServer
{
    private static readonly Guid Id = new("8C4D718D-D94A-42D0-A076-B13A92460E58");
    private static readonly RebarPreviewServer Instance = new();
    private static readonly object Sync = new();
    private static List<(XYZ Start, XYZ End)> _segments = [];
    private static BoundingBoxXYZ? _bounds;

    public static void Register()
    {
        var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService)
            as MultiServerService;
        if (service is null || service.GetRegisteredServerIds().Contains(Id)) return;
        service.AddServer(Instance);
        var active = service.GetActiveServerIds().ToList();
        if (!active.Contains(Id)) active.Add(Id);
        service.SetActiveServers(active);
    }

    public static void Unregister()
    {
        Clear();
        var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService)
            as MultiServerService;
        if (service is null || !service.GetRegisteredServerIds().Contains(Id)) return;
        var active = service.GetActiveServerIds().Where(serverId => serverId != Id).ToList();
        service.SetActiveServers(active);
        service.RemoveServer(Id);
    }

    public static void Show(RebarPlan plan)
    {
        lock (Sync)
        {
            _segments = plan.Bars.SelectMany(PlannedBarGeometry.GetSegments).ToList();
            _bounds = BuildBounds(_segments);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _segments = [];
            _bounds = null;
        }
    }

    public Guid GetServerId() => Id;
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public string GetVendorId() => "Development";
    public string GetName() => "DVB Manhole Rebar Preview";
    public string GetDescription() => "Transient DirectContext3D preview for manhole reinforcement.";
    public string GetApplicationId() => "DVB_ADDIN_MANHOLE_REBAR";
    public string GetSourceId() => string.Empty;
    public bool UsesHandles() => false;
    public bool CanExecute(View view) => _segments.Count > 0 && view is not ViewSchedule;
    public bool UseInTransparentPass(View view) => false;
    public Outline? GetBoundingBox(View view) => _bounds is null ? null : new Outline(_bounds.Min, _bounds.Max);

    public void RenderScene(View view, DisplayStyle displayStyle)
    {
        List<(XYZ Start, XYZ End)> lines;
        lock (Sync) lines = _segments.ToList();
        if (lines.Count == 0) return;

        var vertexCount = lines.Count * 2;
        using var vertexBuffer = new VertexBuffer(vertexCount * VertexPositionColored.GetSizeInFloats());
        vertexBuffer.Map(vertexCount * VertexPositionColored.GetSizeInFloats());
        var vertexStream = vertexBuffer.GetVertexStreamPositionColored();
        var color = new ColorWithTransparency(0, 220, 255, 0);
        foreach (var line in lines)
        {
            vertexStream.AddVertex(new VertexPositionColored(line.Start, color));
            vertexStream.AddVertex(new VertexPositionColored(line.End, color));
        }
        vertexBuffer.Unmap();

        using var indexBuffer = new IndexBuffer(lines.Count * IndexLine.GetSizeInShortInts());
        indexBuffer.Map(lines.Count * IndexLine.GetSizeInShortInts());
        var indexStream = indexBuffer.GetIndexStreamLine();
        for (var index = 0; index < vertexCount; index += 2)
            indexStream.AddLine(new IndexLine(index, index + 1));
        indexBuffer.Unmap();

        var format = new VertexFormat(VertexFormatBits.PositionColored);
        var effect = new EffectInstance(VertexFormatBits.PositionColored);
        DrawContext.FlushBuffer(vertexBuffer, vertexCount, indexBuffer, lines.Count,
            format, effect, PrimitiveType.LineList, 0, lines.Count);
    }

    private static BoundingBoxXYZ? BuildBounds(IReadOnlyCollection<(XYZ Start, XYZ End)> segments)
    {
        if (segments.Count == 0) return null;
        var points = segments.SelectMany(line => new[] { line.Start, line.End }).ToList();
        const double padding = 0.1;
        return new BoundingBoxXYZ
        {
            Min = new XYZ(points.Min(p => p.X) - padding, points.Min(p => p.Y) - padding, points.Min(p => p.Z) - padding),
            Max = new XYZ(points.Max(p => p.X) + padding, points.Max(p => p.Y) + padding, points.Max(p => p.Z) + padding)
        };
    }
}
