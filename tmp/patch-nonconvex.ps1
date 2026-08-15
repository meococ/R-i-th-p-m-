$ErrorActionPreference = 'Stop'
$geomPath = Join-Path (Get-Location) 'Domain\GeometryKernel.cs'
$plannerPath = Join-Path (Get-Location) 'Planning\AbutmentRebarPlanner.cs'
$utf8 = New-Object System.Text.UTF8Encoding $false

$geom = [IO.File]::ReadAllText($geomPath, [Text.Encoding]::UTF8)
if ($geom -match 'PlanPolygonMatLines') {
    Write-Host 'GeometryKernel already has PlanPolygonMatLines'
} else {
    $insertAfter = 'return segments;
    }

    public static double DistancePointToSegment'
    $method = @'
return segments;
    }

    /// <summary>
    /// Scanline mat lines for a simple planar polygon (convex or concave).
    /// Does not fake geometry with a convex hull. Returns every chord on each station.
    /// Inset is applied only when the polygon is convex; otherwise edge cover is left to host clip.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> PlanPolygonMatLines(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 direction,
        double centerlineOffset, double maximumSpacing,
        double startStationClearance, double endStationClearance,
        bool includeFirst, bool includeLast)
    {
        if (maximumSpacing <= 0 || direction.Length <= 1e-12 ||
            startStationClearance < 0 || endStationClearance < 0 ||
            polygon.Count < 3) return [];

        var work = polygon;
        var area = SignedArea(work);
        if (Math.Abs(area) <= 1e-12) return [];
        if (area < 0) work = work.Reverse().ToList();

        var convex = IsConvexPolygon(work);
        if (convex && centerlineOffset > 1e-12)
        {
            var inset = InsetConvexPolygon(work, centerlineOffset);
            if (inset.Count >= 3) work = inset;
        }

        var along = direction.Normalize();
        var stationNormal = new PlanPoint2(-along.Y, along.X);
        var minStation = work.Min(point => point.Dot(stationNormal)) + startStationClearance;
        var maxStation = work.Max(point => point.Dot(stationNormal)) - endStationClearance;
        if (maxStation < minStation - 1e-9) return [];

        var stations = EvenPositions(minStation, maxStation, maximumSpacing).ToList();
        if (!includeLast && stations.Count > 0) stations.RemoveAt(stations.Count - 1);
        if (!includeFirst && stations.Count > 0) stations.RemoveAt(0);
        if (stations.Count == 0) stations.Add((minStation + maxStation) / 2);

        var segments = new List<PlanSegment2>();
        foreach (var station in stations)
        {
            var origin = stationNormal * station;
            foreach (var chord in ClipLineToSimplePolygon(work, origin, along))
                segments.Add(chord);
        }
        return segments;
    }

    public static bool IsConvexPolygon(IReadOnlyList<PlanPoint2> polygon)
    {
        if (polygon.Count < 3) return false;
        var orientation = SignedArea(polygon) >= 0 ? 1d : -1d;
        var signSeen = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var c = polygon[(i + 2) % polygon.Count];
            var cross = orientation * Cross(b - a, c - b);
            if (Math.Abs(cross) <= 1e-10) continue;
            var sign = cross > 0 ? 1 : -1;
            if (signSeen == 0) signSeen = sign;
            else if (sign != signSeen) return false;
        }
        return true;
    }

    /// <summary>
    /// Intersect infinite line with a simple polygon boundary and pair crossings into chords.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> ClipLineToSimplePolygon(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 origin, PlanPoint2 direction)
    {
        if (polygon.Count < 3 || direction.Length <= 1e-12) return [];
        var dir = direction.Normalize();
        var hits = new List<double>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var edge = b - a;
            var den = Cross(dir, edge);
            if (Math.Abs(den) <= 1e-12) continue; // parallel
            var ao = a - origin;
            var t = Cross(ao, edge) / den; // along line
            var u = Cross(ao, dir) / den;  // along edge
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            hits.Add(t);
        }

        hits.Sort();
        // Unique nearly-equal hits (vertices shared by two edges)
        var unique = new List<double>();
        foreach (var hit in hits)
        {
            if (unique.Count == 0 || Math.Abs(unique[^1] - hit) > 1e-7)
                unique.Add(hit);
        }

        var segments = new List<PlanSegment2>();
        for (var i = 0; i + 1 < unique.Count; i += 2)
        {
            var t0 = unique[i];
            var t1 = unique[i + 1];
            if (t1 - t0 <= 1e-8) continue;
            // Midpoint sample keeps only interior chords
            var mid = origin + dir * ((t0 + t1) * 0.5);
            if (!IsInsideSimplePolygon(polygon, mid)) continue;
            segments.Add(new PlanSegment2(origin + dir * t0, origin + dir * t1));
        }
        return segments;
    }

    public static bool IsInsideSimplePolygon(IReadOnlyList<PlanPoint2> polygon, PlanPoint2 point)
    {
        if (polygon.Count < 3) return false;
        // Ray casting to +X
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var intersect = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                            (point.X < (b.X - a.X) * (point.Y - a.Y) / ((b.Y - a.Y) + 0.0) + a.X);
            if (intersect) inside = !inside;
        }
        // Boundary counts as inside for station stability
        if (inside) return true;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (DistancePointToSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]) <= 1e-7)
                return true;
        }
        return false;
    }

    public static double DistancePointToSegment
'@
    if (-not $geom.Contains('return segments;')) { throw 'anchor missing' }
    # replace only first PlanConvexMatLines closing + DistancePointToSegment
    $idx = $geom.IndexOf('public static IReadOnlyList<PlanSegment2> PlanConvexMatLines')
    if ($idx -lt 0) { throw 'PlanConvexMatLines missing' }
    $distIdx = $geom.IndexOf('public static double DistancePointToSegment', $idx)
    if ($distIdx -lt 0) { throw 'DistancePointToSegment missing' }
    $before = $geom.Substring(0, $distIdx)
    $after = $geom.Substring($distIdx)
    # ensure before ends with PlanConvexMatLines method fully
    $geom = $before + @'
public static IReadOnlyList<PlanSegment2> PlanPolygonMatLines(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 direction,
        double centerlineOffset, double maximumSpacing,
        double startStationClearance, double endStationClearance,
        bool includeFirst, bool includeLast)
    {
        if (maximumSpacing <= 0 || direction.Length <= 1e-12 ||
            startStationClearance < 0 || endStationClearance < 0 ||
            polygon.Count < 3) return [];

        var work = polygon.ToList();
        var area = SignedArea(work);
        if (Math.Abs(area) <= 1e-12) return [];
        if (area < 0) work.Reverse();

        var convex = IsConvexPolygon(work);
        if (convex && centerlineOffset > 1e-12)
        {
            var inset = InsetConvexPolygon(work, centerlineOffset);
            if (inset.Count >= 3) work = inset.ToList();
        }

        var along = direction.Normalize();
        var stationNormal = new PlanPoint2(-along.Y, along.X);
        var minStation = work.Min(point => point.Dot(stationNormal)) + startStationClearance;
        var maxStation = work.Max(point => point.Dot(stationNormal)) - endStationClearance;
        if (maxStation < minStation - 1e-9) return [];

        var stations = EvenPositions(minStation, maxStation, maximumSpacing).ToList();
        if (!includeLast && stations.Count > 0) stations.RemoveAt(stations.Count - 1);
        if (!includeFirst && stations.Count > 0) stations.RemoveAt(0);
        if (stations.Count == 0) stations.Add((minStation + maxStation) / 2);

        var segments = new List<PlanSegment2>();
        foreach (var station in stations)
        {
            var origin = stationNormal * station;
            foreach (var chord in ClipLineToSimplePolygon(work, origin, along))
                segments.Add(chord);
        }
        return segments;
    }

    public static bool IsConvexPolygon(IReadOnlyList<PlanPoint2> polygon)
    {
        if (polygon.Count < 3) return false;
        var orientation = SignedArea(polygon) >= 0 ? 1d : -1d;
        var signSeen = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var c = polygon[(i + 2) % polygon.Count];
            var cross = orientation * Cross(b - a, c - b);
            if (Math.Abs(cross) <= 1e-10) continue;
            var sign = cross > 0 ? 1 : -1;
            if (signSeen == 0) signSeen = sign;
            else if (sign != signSeen) return false;
        }
        return true;
    }

    public static IReadOnlyList<PlanSegment2> ClipLineToSimplePolygon(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 origin, PlanPoint2 direction)
    {
        if (polygon.Count < 3 || direction.Length <= 1e-12) return [];
        var dir = direction.Normalize();
        var hits = new List<double>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var edge = b - a;
            var den = Cross(dir, edge);
            if (Math.Abs(den) <= 1e-12) continue;
            var ao = a - origin;
            var t = Cross(ao, edge) / den;
            var u = Cross(ao, dir) / den;
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            hits.Add(t);
        }

        hits.Sort();
        var unique = new List<double>();
        foreach (var hit in hits)
        {
            if (unique.Count == 0 || Math.Abs(unique[^1] - hit) > 1e-7)
                unique.Add(hit);
        }

        var segments = new List<PlanSegment2>();
        for (var i = 0; i + 1 < unique.Count; i += 2)
        {
            var t0 = unique[i];
            var t1 = unique[i + 1];
            if (t1 - t0 <= 1e-8) continue;
            var mid = origin + dir * ((t0 + t1) * 0.5);
            if (!IsInsideSimplePolygon(polygon, mid)) continue;
            segments.Add(new PlanSegment2(origin + dir * t0, origin + dir * t1));
        }
        return segments;
    }

    public static bool IsInsideSimplePolygon(IReadOnlyList<PlanPoint2> polygon, PlanPoint2 point)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var denom = (b.Y - a.Y);
            var intersect = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                            (point.X < (b.X - a.X) * (point.Y - a.Y) / (Math.Abs(denom) < 1e-18 ? 1e-18 : denom) + a.X);
            if (intersect) inside = !inside;
        }
        if (inside) return true;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (DistancePointToSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]) <= 1e-7)
                return true;
        }
        return false;
    }

    '@ + $after
    [IO.File]::WriteAllText($geomPath, $geom, $utf8)
    Write-Host 'GeometryKernel patched'
}

# Planner surface strategy: use outer loop + PlanPolygonMatLines
$planner = [IO.File]::ReadAllText($plannerPath, [Text.Encoding]::UTF8)
$old = @'
        var outer = loopPolygons[0];
        var hull = GeometryKernel.ConvexHull(outer);
        var outerArea = Math.Abs(GeometryKernel.SignedArea(outer));
        var hullArea = Math.Abs(GeometryKernel.SignedArea(hull));
        if (hull.Count < 3 || outerArea <= 1e-9 ||
            hullArea - outerArea > Math.Max(outerArea * 0.005, 1e-8))
        {
            error = "ABUTMENT_SURFACE_NONCONVEX_UNSUPPORTED: outer loop lõm; không dùng convex hull giả để tạo thép.";
            return false;
        }

        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var spacing = rule.SpacingMm * mm;
        if (spacing <= 1e-9)
        {
            error = "Surface spacing must be > 0.";
            return false;
        }
        var along = rule.GeometryTemplate is
            AbutmentGeometryTemplate.VerticalFront or
            AbutmentGeometryTemplate.VerticalBack
            ? new PlanPoint2(0, 1)
            : new PlanPoint2(1, 0);
        var seedSegments = GeometryKernel.PlanConvexMatLines(
            hull,
            along,
            centerlineOffset,
            spacing,
            rule.StartStationClearanceMm * mm,
            rule.EndStationClearanceMm * mm,
            rule.IncludeFirst,
            rule.IncludeLast);
'@

$new = @'
        var outer = loopPolygons[0];
        if (outer.Count < 3 || Math.Abs(GeometryKernel.SignedArea(outer)) <= 1e-9)
        {
            error = "ABUTMENT_SURFACE_BOUNDARY_UNRESOLVED: outer loop khong hop le.";
            return false;
        }

        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var spacing = rule.SpacingMm * mm;
        if (spacing <= 1e-9)
        {
            error = "Surface spacing must be > 0.";
            return false;
        }
        var along = rule.GeometryTemplate is
            AbutmentGeometryTemplate.VerticalFront or
            AbutmentGeometryTemplate.VerticalBack
            ? new PlanPoint2(0, 1)
            : new PlanPoint2(1, 0);
        // Use true outer loop (convex or concave). No convex-hull substitute.
        var edgeInset = GeometryKernel.IsConvexPolygon(outer) ? centerlineOffset : 0d;
        var seedSegments = GeometryKernel.PlanPolygonMatLines(
            outer,
            along,
            edgeInset,
            spacing,
            rule.StartStationClearanceMm * mm,
            rule.EndStationClearanceMm * mm,
            rule.IncludeFirst,
            rule.IncludeLast);
'@

if ($planner.Contains('ABUTMENT_SURFACE_NONCONVEX_UNSUPPORTED')) {
    # normalize newlines
    $plannerN = $planner -replace "`r`n", "`n"
    $oldN = $old -replace "`r`n", "`n"
    $newN = $new -replace "`r`n", "`n"
    if (-not $plannerN.Contains($oldN)) {
        # try without Vietnamese
        throw 'planner surface block not found exactly'
    }
    $plannerN = $plannerN.Replace($oldN, $newN)
    [IO.File]::WriteAllText($plannerPath, ($plannerN -replace "`n", "`r`n"), $utf8)
    Write-Host 'Planner surface patched'
} else {
    Write-Host 'Planner already without nonconvex hard fail'
}

Write-Host 'DONE'
