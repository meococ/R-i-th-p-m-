using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Models;
using MediaColor = System.Windows.Media.Color;
using RevitMesh = Autodesk.Revit.DB.Mesh;
using WpfGrid = System.Windows.Controls.Grid;
using WpfMaterial = System.Windows.Media.Media3D.Material;
using WpfPoint = System.Windows.Point;

namespace BIM.DatViet.Views.Controls;

/// <summary>
/// Passive 3D evidence view for the classified host, canonical zone and proposed rebar.
/// Navigation is limited to orbit, pan and zoom; zone identity is never authored in the viewport.
/// </summary>
public sealed class AbutmentZoneViewport3D : WpfGrid
{
    private const int CylinderSides = 6;
    private const int MaxPreviewBarsPerRule = 14;

    private static readonly WpfMaterial HostSurfaceMaterial = CreateMaterial(120, 145, 160, 24);
    private static readonly WpfMaterial HostEdgeMaterial = CreateMaterial(132, 163, 174, 180);
    private static readonly WpfMaterial ZoneSurfaceMaterial = CreateMaterial(93, 130, 143, 42);
    private static readonly WpfMaterial ZoneEdgeMaterial = CreateMaterial(0, 217, 232, 255);
    private static readonly WpfMaterial BottomLongitudinalBarMaterial = CreateMaterial(24, 205, 225, 255);
    private static readonly WpfMaterial BottomTransverseBarMaterial = CreateMaterial(255, 178, 61, 255);
    private static readonly WpfMaterial TopLongitudinalBarMaterial = CreateMaterial(239, 91, 137, 255);
    private static readonly WpfMaterial TopTransverseBarMaterial = CreateMaterial(115, 201, 104, 255);
    private static readonly WpfMaterial OtherBarMaterial = CreateMaterial(157, 123, 255, 255);

    private readonly Viewport3D _viewport = new();
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 34 };
    private readonly Model3DGroup _root = new();
    private readonly Model3DGroup _hostGroup = new();
    private readonly Model3DGroup _edgeGroup = new();
    private readonly Model3DGroup _zoneGroup = new();
    private readonly Model3DGroup _rebarGroup = new();
    private readonly Border _keyboardFocusBorder;

    private AbutmentHostContext? _context;
    private AbutmentViewportRenderMode _renderMode = AbutmentViewportRenderMode.XRay;
    private AbutmentLocalBox? _focusedBox;
    private XYZ _sceneCenter = XYZ.Zero;
    private double _sceneExtent = 1;
    private double _yaw = 0.78;
    private double _pitch = 0.42;
    private double _distance = 10;
    private Vector3D _target;
    private WpfPoint _lastPointer;
    private bool _leftDragging;
    private bool _rightDragging;
    private bool _displayingRebars;

    public AbutmentZoneViewport3D()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = new SolidColorBrush(MediaColor.FromRgb(24, 36, 43));
        AutomationProperties.SetHelpText(this,
            "Mũi tên để xoay, Shift cộng mũi tên để dịch chuyển, dấu cộng hoặc trừ để thu phóng.");

        _root.Children.Add(new AmbientLight(MediaColor.FromRgb(118, 126, 130)));
        _root.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.45, -0.75, -0.6)));
        _root.Children.Add(new DirectionalLight(MediaColor.FromRgb(150, 185, 198), new Vector3D(0.5, -0.15, 0.75)));
        _root.Children.Add(_hostGroup);
        _root.Children.Add(_edgeGroup);
        _root.Children.Add(_zoneGroup);
        _root.Children.Add(_rebarGroup);

        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = _root });
        Children.Add(_viewport);

        var focusBrush = new SolidColorBrush(MediaColor.FromRgb(67, 198, 213));
        focusBrush.Freeze();
        _keyboardFocusBorder = new Border
        {
            BorderBrush = focusBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = System.Windows.Visibility.Collapsed
        };
        System.Windows.Controls.Panel.SetZIndex(_keyboardFocusBorder, 10);
        Children.Add(_keyboardFocusBorder);

        _viewport.MouseLeftButtonDown += OnLeftButtonDown;
        _viewport.MouseLeftButtonUp += OnLeftButtonUp;
        _viewport.MouseRightButtonDown += OnRightButtonDown;
        _viewport.MouseRightButtonUp += OnRightButtonUp;
        _viewport.MouseMove += OnMouseMove;
        _viewport.MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        GotKeyboardFocus += (_, _) => UpdateKeyboardVisuals();
        LostKeyboardFocus += (_, _) => UpdateKeyboardVisuals();
        SizeChanged += (_, _) =>
        {
            if (_focusedBox is not null) FocusOnBox(_focusedBox);
            else if (_context is not null) FocusOnOverview();
        };
        UpdateCamera();
    }

    /// <summary>Changes only the passive preview presentation; it never changes the Revit document or active view.</summary>
    public void SetRenderMode(AbutmentViewportRenderMode renderMode)
    {
        if (_renderMode == renderMode) return;
        _renderMode = renderMode;
        ApplyHostRenderMode();
    }

    private void ApplyHostRenderMode()
    {
        WpfMaterial? material = _displayingRebars ||
                                _renderMode == AbutmentViewportRenderMode.Wireframe
            ? null
            : HostSurfaceMaterial;
        foreach (var model in _hostGroup.Children.OfType<GeometryModel3D>())
        {
            model.Material = material;
            model.BackMaterial = material;
        }
    }

    public bool LoadScene(
        AbutmentHostContext context,
        IReadOnlyList<AbutmentZoneDescriptor> zones,
        bool preserveSelection = false)
    {
        _ = zones;
        _ = preserveSelection;
        if (!context.OverallBox.IsValid ||
            !IsFinite(context.OverallBox.Min) ||
            !IsFinite(context.OverallBox.Max))
        {
            throw new InvalidOperationException(
                "Không thể dựng mô hình 3D vì bounding box của host không hợp lệ.");
        }

        _context = context;
        _displayingRebars = false;
        _focusedBox = null;
        _sceneCenter = context.OverallBox.Center;
        _sceneExtent = Math.Max(1, Math.Sqrt(
            context.OverallBox.Length * context.OverallBox.Length +
            context.OverallBox.Depth * context.OverallBox.Depth +
            context.OverallBox.Height * context.OverallBox.Height));
        _hostGroup.Children.Clear();
        _edgeGroup.Children.Clear();
        _zoneGroup.Children.Clear();
        _rebarGroup.Children.Clear();

        var renderedFaceCount = 0;
        for (var solidIndex = 0; solidIndex < context.Solids.Count; solidIndex++)
        {
            var solid = context.Solids[solidIndex].Solid;
            var faces = solid.Faces.Cast<Face>().ToList();
            for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                try
                {
                    var mesh = BuildFaceMesh(faces[faceIndex].Triangulate());
                    if (mesh.Positions.Count == 0) continue;
                    _hostGroup.Children.Add(new GeometryModel3D(mesh, HostSurfaceMaterial)
                    {
                        BackMaterial = HostSurfaceMaterial
                    });
                    renderedFaceCount++;
                }
                catch (Exception exception)
                {
                    AbutmentDiagnostics.LogException(
                        $"ABUTMENT_VIEWPORT_FACE_SKIPPED_S{solidIndex}_F{faceIndex}", exception);
                }
            }

            foreach (Edge edge in solid.Edges)
            {
                try
                {
                    var localPoints = edge.Tessellate()
                        .Select(context.Frame.ToLocal)
                        .Where(IsFinite)
                        .ToList();
                    if (localPoints.Count < 2) continue;
                    var mesh = BuildEdgeMesh(localPoints, _sceneExtent * 0.00065);
                    if (mesh.Positions.Count == 0) continue;
                    _edgeGroup.Children.Add(new GeometryModel3D(mesh, HostEdgeMaterial)
                    {
                        BackMaterial = HostEdgeMaterial
                    });
                }
                catch (Exception exception)
                {
                    AbutmentDiagnostics.LogException(
                        $"ABUTMENT_VIEWPORT_EDGE_SKIPPED_S{solidIndex}", exception);
                }
            }
        }

        if (renderedFaceCount == 0)
        {
            throw new InvalidOperationException(
                "Không dựng được mặt host nào cho mô hình 3D. Hình học Revit có thể có mặt không hợp lệ.");
        }

        _target = new Vector3D();
        _yaw = 0.78;
        ApplyHostRenderMode();
        _pitch = 0.42;
        FocusOnOverview();
        return true;
    }

    public void ClearSelection()
    {
        _focusedBox = null;
        _zoneGroup.Children.Clear();
        _rebarGroup.Children.Clear();
        _displayingRebars = false;
        ApplyHostRenderMode();
        if (_context is not null) FocusOnOverview();
    }

    public void ShowSelection(
        AbutmentZoneDescriptor zone,
        AbutmentRebarPlan? plan,
        bool showRebars)
    {
        if (_context is null) return;
        _zoneGroup.Children.Clear();
        _rebarGroup.Children.Clear();

        _displayingRebars = showRebars &&
                            plan is not null &&
                            plan.Bars.Any(bar => bar.Zone == zone.Kind);
        ApplyHostRenderMode();
        AddCanonicalZoneMeshes(zone, includeSurfaces: !_displayingRebars);
        if (_displayingRebars)
            AddRebarMeshes(plan!.Bars.Where(bar => bar.Zone == zone.Kind));
        FocusOnBox(zone.LocalBox.IsValid ? zone.LocalBox : _context.OverallBox);
    }

    private void AddCanonicalZoneMeshes(AbutmentZoneDescriptor zone, bool includeSurfaces)
    {
        var box = zone.LocalBox.IsValid ? zone.LocalBox : _context!.OverallBox;
        if (zone.ProfileGeometry is { Sections.Count: >= 2 } profileGeometry &&
            TryAddProfileZoneMeshes(profileGeometry, includeSurfaces))
            return;
        if (zone.SurfacePair is { } surfacePair)
        {
            foreach (var face in new[] { surfacePair.FrontFace, surfacePair.BackFace })
            {
                if (includeSurfaces)
                    AddModel(_zoneGroup, BuildCachedFaceMesh(face.PreviewTrianglesLocal), ZoneSurfaceMaterial);
                foreach (var loop in face.BoundaryLoops)
                {
                    var localPoints = loop.Select(_context!.Frame.ToLocal).ToList();
                    if (localPoints.Count < 2) continue;
                    if (localPoints[0].DistanceTo(localPoints[^1]) > 1e-9)
                        localPoints.Add(localPoints[0]);
                    var edgeMesh = BuildEdgeMesh(localPoints, _sceneExtent * 0.00105);
                    AddModel(_zoneGroup, edgeMesh, ZoneEdgeMaterial);
                }
            }
            return;
        }

        if (zone.Kind == AbutmentZoneKind.Footing &&
            _context!.FootingBoundary is { Vertices.Count: >= 3 } boundary)
        {
            var polygon = boundary.Vertices
                .Select(_context.Frame.ToLocal)
                .Select(point => new XYZ(point.X, point.Y, 0))
                .ToList();
            if (polygon.Count > 1 && polygon[0].DistanceTo(polygon[^1]) <= 1e-9)
                polygon.RemoveAt(polygon.Count - 1);
            if (polygon.Count >= 3)
            {
                AddPrismMeshes(polygon, box.Min.Z, box.Max.Z, includeSurfaces);
                return;
            }
        }

        var min = box.Min;
        var max = box.Max;
        AddPrismMeshes(
        [
            new XYZ(min.X, min.Y, 0),
            new XYZ(max.X, min.Y, 0),
            new XYZ(max.X, max.Y, 0),
            new XYZ(min.X, max.Y, 0)
        ], box.Min.Z, box.Max.Z, includeSurfaces);
    }

    private bool TryAddProfileZoneMeshes(
        AbutmentProfileZoneGeometry geometry,
        bool includeSurfaces)
    {
        var sections = geometry.Sections
            .OrderBy(section => section.StationCoordinate)
            .ToArray();
        var vertexCount = sections[0].OrderedPolygonLocal.Count;
        if (vertexCount < 3 || sections.Any(section => section.OrderedPolygonLocal.Count != vertexCount))
            return false;

        var surfaceMesh = new MeshGeometry3D();
        var outlineMesh = new MeshGeometry3D();
        for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            var polygon = sections[sectionIndex].OrderedPolygonLocal;
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                var next = (vertex + 1) % vertexCount;
                AppendCylinder(
                    outlineMesh,
                    ToRender(polygon[vertex]),
                    ToRender(polygon[next]),
                    _sceneExtent * 0.00105);
            }
        }

        for (var sectionIndex = 0; sectionIndex < sections.Length - 1; sectionIndex++)
        {
            var first = sections[sectionIndex].OrderedPolygonLocal;
            var second = sections[sectionIndex + 1].OrderedPolygonLocal;
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                var next = (vertex + 1) % vertexCount;
                AddTriangle(surfaceMesh,
                    ToRender(first[vertex]), ToRender(first[next]), ToRender(second[next]));
                AddTriangle(surfaceMesh,
                    ToRender(first[vertex]), ToRender(second[next]), ToRender(second[vertex]));
                AppendCylinder(
                    outlineMesh,
                    ToRender(first[vertex]),
                    ToRender(second[vertex]),
                    _sceneExtent * 0.00105);
            }
        }

        if (includeSurfaces) AddModel(_zoneGroup, surfaceMesh, ZoneSurfaceMaterial);
        AddModel(_zoneGroup, outlineMesh, ZoneEdgeMaterial);
        return true;
    }

    private void AddPrismMeshes(
        IReadOnlyList<XYZ> polygon,
        double bottom,
        double top,
        bool includeSurface)
    {
        var solidMesh = new MeshGeometry3D();
        var outlineMesh = new MeshGeometry3D();
        var bottomVertices = polygon.Select(point => new XYZ(point.X, point.Y, bottom)).ToList();
        var topVertices = polygon.Select(point => new XYZ(point.X, point.Y, top)).ToList();

        for (var index = 1; index < polygon.Count - 1; index++)
        {
            AddTriangle(solidMesh,
                ToRender(bottomVertices[0]), ToRender(bottomVertices[index + 1]), ToRender(bottomVertices[index]));
            AddTriangle(solidMesh,
                ToRender(topVertices[0]), ToRender(topVertices[index]), ToRender(topVertices[index + 1]));
        }

        for (var index = 0; index < polygon.Count; index++)
        {
            var next = (index + 1) % polygon.Count;
            AddTriangle(solidMesh,
                ToRender(bottomVertices[index]), ToRender(bottomVertices[next]), ToRender(topVertices[next]));
            AddTriangle(solidMesh,
                ToRender(bottomVertices[index]), ToRender(topVertices[next]), ToRender(topVertices[index]));
            AppendCylinder(outlineMesh, ToRender(bottomVertices[index]), ToRender(bottomVertices[next]), _sceneExtent * 0.00105);
            AppendCylinder(outlineMesh, ToRender(topVertices[index]), ToRender(topVertices[next]), _sceneExtent * 0.00105);
            AppendCylinder(outlineMesh, ToRender(bottomVertices[index]), ToRender(topVertices[index]), _sceneExtent * 0.00105);
        }

        if (includeSurface) AddModel(_zoneGroup, solidMesh, ZoneSurfaceMaterial);
        AddModel(_zoneGroup, outlineMesh, ZoneEdgeMaterial);
    }

    private MeshGeometry3D BuildFaceMesh(RevitMesh source)
    {
        var result = new MeshGeometry3D();
        for (var index = 0; index < source.NumTriangles; index++)
        {
            var triangle = source.get_Triangle(index);
            AddTriangle(result,
                ToRender(_context!.Frame.ToLocal(triangle.get_Vertex(0))),
                ToRender(_context.Frame.ToLocal(triangle.get_Vertex(1))),
                ToRender(_context.Frame.ToLocal(triangle.get_Vertex(2))));
        }
        if (result.Positions.Count > 0) result.Freeze();
        return result;
    }

    private MeshGeometry3D BuildCachedFaceMesh(IReadOnlyList<XYZ> localTriangleVertices)
    {
        var result = new MeshGeometry3D();
        for (var index = 0; index + 2 < localTriangleVertices.Count; index += 3)
        {
            AddTriangle(result,
                ToRender(localTriangleVertices[index]),
                ToRender(localTriangleVertices[index + 1]),
                ToRender(localTriangleVertices[index + 2]));
        }
        if (result.Positions.Count > 0) result.Freeze();
        return result;
    }

    private MeshGeometry3D BuildEdgeMesh(IReadOnlyList<XYZ> localPoints, double radius)
    {
        var mesh = new MeshGeometry3D();
        for (var index = 1; index < localPoints.Count; index++)
            AppendCylinder(mesh, ToRender(localPoints[index - 1]), ToRender(localPoints[index]), radius);
        if (mesh.Positions.Count > 0) mesh.Freeze();
        return mesh;
    }

    private void AddRebarMeshes(IEnumerable<PlannedAbutmentRebar> bars)
    {
        var meshesByMaterial = new Dictionary<WpfMaterial, MeshGeometry3D>();
        foreach (var bar in SampleBarsForPreview(bars))
        {
            var material = GetBarMaterial(bar.Rule.GeometryTemplate);
            if (!meshesByMaterial.TryGetValue(material, out var mesh))
            {
                mesh = new MeshGeometry3D();
                meshesByMaterial.Add(material, mesh);
            }

            var actualRadius = UnitUtils.ConvertToInternalUnits(
                bar.Rule.DiameterMm / 2, UnitTypeId.Millimeters);
            var radius = Math.Clamp(
                actualRadius * 2.5,
                _sceneExtent * 0.0012,
                _sceneExtent * 0.0025);
            for (var index = 1; index < bar.Points.Count; index++)
            {
                AppendCylinder(mesh,
                    ToRender(_context!.Frame.ToLocal(bar.Points[index - 1])),
                    ToRender(_context.Frame.ToLocal(bar.Points[index])),
                    radius);
            }
        }

        foreach (var (material, mesh) in meshesByMaterial)
            AddModel(_rebarGroup, mesh, material);
    }

    private static IEnumerable<PlannedAbutmentRebar> SampleBarsForPreview(
        IEnumerable<PlannedAbutmentRebar> bars)
    {
        foreach (var group in bars
                     .GroupBy(bar => bar.Rule.RuleId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(bar => bar.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (ordered.Count <= MaxPreviewBarsPerRule)
            {
                foreach (var bar in ordered) yield return bar;
                continue;
            }

            for (var index = 0; index < MaxPreviewBarsPerRule; index++)
            {
                var sourceIndex = (int)Math.Round(
                    index * (ordered.Count - 1d) / (MaxPreviewBarsPerRule - 1d));
                yield return ordered[sourceIndex];
            }
        }
    }

    private static WpfMaterial GetBarMaterial(AbutmentGeometryTemplate template) => template switch
    {
        AbutmentGeometryTemplate.BottomLongitudinal => BottomLongitudinalBarMaterial,
        AbutmentGeometryTemplate.BottomTransverse => BottomTransverseBarMaterial,
        AbutmentGeometryTemplate.TopLongitudinal => TopLongitudinalBarMaterial,
        AbutmentGeometryTemplate.TopTransverse => TopTransverseBarMaterial,
        _ => OtherBarMaterial
    };

    internal static Brush GetBarPreviewBrush(AbutmentGeometryTemplate template) =>
        ((DiffuseMaterial)GetBarMaterial(template)).Brush;

    private static void AddModel(Model3DGroup group, MeshGeometry3D mesh, WpfMaterial material)
    {
        if (mesh.Positions.Count == 0) return;
        if (!mesh.IsFrozen) mesh.Freeze();
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static void AppendCylinder(MeshGeometry3D mesh, Point3D start, Point3D end, double radius)
    {
        if (!IsFinite(start) || !IsFinite(end) || !double.IsFinite(radius) || radius <= 0) return;
        var direction = end - start;
        if (!double.IsFinite(direction.Length) || direction.Length < 1e-8) return;
        direction.Normalize();
        var reference = Math.Abs(Vector3D.DotProduct(direction, new Vector3D(0, 1, 0))) < 0.9
            ? new Vector3D(0, 1, 0)
            : new Vector3D(1, 0, 0);
        var radialU = Vector3D.CrossProduct(direction, reference);
        radialU.Normalize();
        var radialV = Vector3D.CrossProduct(direction, radialU);
        radialV.Normalize();

        var baseIndex = mesh.Positions.Count;
        for (var side = 0; side < CylinderSides; side++)
        {
            var angle = 2 * Math.PI * side / CylinderSides;
            var normal = radialU * Math.Cos(angle) + radialV * Math.Sin(angle);
            mesh.Positions.Add(start + normal * radius);
            mesh.Positions.Add(end + normal * radius);
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
        }
        for (var side = 0; side < CylinderSides; side++)
        {
            var next = (side + 1) % CylinderSides;
            var a = baseIndex + side * 2;
            var b = baseIndex + next * 2;
            mesh.TriangleIndices.Add(a);
            mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(b + 1);
            mesh.TriangleIndices.Add(a);
            mesh.TriangleIndices.Add(b + 1);
            mesh.TriangleIndices.Add(a + 1);
        }
    }

    private static void AddTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    {
        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c)) return;
        var normal = Vector3D.CrossProduct(b - a, c - a);
        if (!double.IsFinite(normal.Length) || normal.Length < 1e-10) return;
        normal.Normalize();
        var index = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 1);
        mesh.TriangleIndices.Add(index + 2);
    }

    private Point3D ToRender(XYZ local) => new(
        local.X - _sceneCenter.X,
        local.Z - _sceneCenter.Z,
        -(local.Y - _sceneCenter.Y));

    private void FocusOnBox(AbutmentLocalBox box)
    {
        _focusedBox = box;
        var center = ToRender(box.Center);
        _target = new Vector3D(center.X, center.Y, center.Z);
        _distance = CalculateFitDistance(GetRenderBoxCorners(box), 1.10);
        UpdateCamera();
    }

    private void FocusOnOverview()
    {
        _focusedBox = null;
        _target = new Vector3D();
        _distance = _context is null
            ? _sceneExtent * 1.65
            : CalculateFitDistance(GetRenderBoxCorners(_context.OverallBox), 1.12);
        UpdateCamera();
    }

    private double CalculateFitDistance(IReadOnlyList<Point3D> points, double padding)
    {
        var horizontal = Math.Cos(_pitch);
        var forward = new Vector3D(
            -horizontal * Math.Sin(_yaw),
            -Math.Sin(_pitch),
            -horizontal * Math.Cos(_yaw));
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();

        var aspect = _viewport.ActualHeight > 1
            ? Math.Max(0.1, _viewport.ActualWidth / _viewport.ActualHeight)
            : 1;
        var horizontalTangent = Math.Max(1e-6, Math.Tan(_camera.FieldOfView * Math.PI / 360));
        var verticalTangent = Math.Max(1e-6, horizontalTangent / aspect);
        var target = new Point3D(_target.X, _target.Y, _target.Z);
        var requiredDistance = 0d;

        foreach (var point in points)
        {
            if (!IsFinite(point)) continue;
            var offset = point - target;
            var depthOffset = Vector3D.DotProduct(offset, forward);
            requiredDistance = Math.Max(requiredDistance,
                Math.Abs(Vector3D.DotProduct(offset, right)) / horizontalTangent - depthOffset);
            requiredDistance = Math.Max(requiredDistance,
                Math.Abs(Vector3D.DotProduct(offset, up)) / verticalTangent - depthOffset);
        }

        var fittedDistance = requiredDistance * padding;
        if (!double.IsFinite(fittedDistance) || fittedDistance <= 0)
            fittedDistance = _sceneExtent * 1.65;
        return Math.Clamp(fittedDistance, _sceneExtent * 0.08, _sceneExtent * 20);
    }

    private Point3D[] GetRenderBoxCorners(AbutmentLocalBox box)
    {
        var min = box.Min;
        var max = box.Max;
        return
        [
            ToRender(new XYZ(min.X, min.Y, min.Z)),
            ToRender(new XYZ(min.X, min.Y, max.Z)),
            ToRender(new XYZ(min.X, max.Y, min.Z)),
            ToRender(new XYZ(min.X, max.Y, max.Z)),
            ToRender(new XYZ(max.X, min.Y, min.Z)),
            ToRender(new XYZ(max.X, min.Y, max.Z)),
            ToRender(new XYZ(max.X, max.Y, min.Z)),
            ToRender(new XYZ(max.X, max.Y, max.Z))
        ];
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_context is null) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left:
                if (shift) PanCamera(-24, 0);
                else _yaw += 0.08;
                break;
            case Key.Right:
                if (shift) PanCamera(24, 0);
                else _yaw -= 0.08;
                break;
            case Key.Up:
                if (shift) PanCamera(0, -24);
                else _pitch = Math.Clamp(_pitch - 0.08, -1.35, 1.35);
                break;
            case Key.Down:
                if (shift) PanCamera(0, 24);
                else _pitch = Math.Clamp(_pitch + 0.08, -1.35, 1.35);
                break;
            case Key.Add:
            case Key.OemPlus:
                _distance = Math.Clamp(_distance * 0.86, _sceneExtent * 0.08, _sceneExtent * 20);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                _distance = Math.Clamp(_distance * 1.16, _sceneExtent * 0.08, _sceneExtent * 20);
                break;
            case Key.Home:
                _yaw = 0.78;
                _pitch = 0.42;
                FocusOnOverview();
                e.Handled = true;
                return;
            default:
                return;
        }
        UpdateCamera();
        e.Handled = true;
    }

    private void PanCamera(double horizontalPixels, double verticalPixels)
    {
        var look = _camera.LookDirection;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, _camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();
        var scale = _distance / Math.Max(240, _viewport.ActualWidth);
        _target += right * (-horizontalPixels * scale) + up * (verticalPixels * scale);
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _leftDragging = true;
        _lastPointer = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_leftDragging) return;
        _leftDragging = false;
        _viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _rightDragging = true;
        _lastPointer = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _rightDragging = false;
        _viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(_viewport);
        var delta = point - _lastPointer;
        _lastPointer = point;
        if (_leftDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            _yaw -= delta.X * 0.008;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.008, -1.35, 1.35);
            UpdateCamera();
            e.Handled = true;
        }
        else if (_rightDragging && e.RightButton == MouseButtonState.Pressed)
        {
            PanCamera(delta.X, delta.Y);
            UpdateCamera();
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(
            _distance * (e.Delta > 0 ? 0.86 : 1.16),
            _sceneExtent * 0.08,
            _sceneExtent * 20);
        UpdateCamera();
        e.Handled = true;
    }

    private void UpdateCamera()
    {
        if (!double.IsFinite(_distance) || _distance <= 0)
            _distance = Math.Max(1, _sceneExtent * 1.65);
        if (!double.IsFinite(_target.X) || !double.IsFinite(_target.Y) || !double.IsFinite(_target.Z))
            _target = new Vector3D();
        var horizontal = _distance * Math.Cos(_pitch);
        var offset = new Vector3D(
            horizontal * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch),
            horizontal * Math.Cos(_yaw));
        var target = new Point3D(_target.X, _target.Y, _target.Z);
        _camera.Position = target + offset;
        _camera.LookDirection = target - _camera.Position;
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void UpdateKeyboardVisuals() =>
        _keyboardFocusBorder.Visibility = IsKeyboardFocusWithin
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    private static bool IsFinite(XYZ point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static bool IsFinite(Point3D point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AbutmentZoneViewportAutomationPeer(this);

    private sealed class AbutmentZoneViewportAutomationPeer(AbutmentZoneViewport3D owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(AbutmentZoneViewport3D);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrWhiteSpace(name) ? "Khung kiểm tra mố cầu 3D" : name;
        }

        protected override string GetHelpTextCore() =>
            "Dùng chuột hoặc bàn phím để xoay, dịch chuyển và thu phóng mô hình; viewport chỉ hiển thị evidence.";

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Custom;
    }

    private static WpfMaterial CreateMaterial(byte red, byte green, byte blue, byte alpha)
    {
        var brush = new SolidColorBrush(MediaColor.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
}
