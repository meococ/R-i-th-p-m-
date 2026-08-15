$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false
$geomPath = 'Domain\GeometryKernel.cs'
$plannerPath = 'Planning\AbutmentRebarPlanner.cs'
$fragPath = 'tmp\GeometryKernel.NonConvex.cs.fragment'

$geom = [IO.File]::ReadAllText((Resolve-Path $geomPath), [Text.Encoding]::UTF8)
if ($geom -match 'PlanPolygonMatLines') {
    Write-Host 'Geometry already patched'
} else {
    $frag = [IO.File]::ReadAllText((Resolve-Path $fragPath), [Text.Encoding]::UTF8)
    $marker = 'public static double DistancePointToSegment'
    $idx = $geom.IndexOf($marker)
    if ($idx -lt 0) { throw 'marker not found' }
    $geom = $geom.Insert($idx, $frag + "`r`n    ")
    [IO.File]::WriteAllText((Resolve-Path $geomPath), $geom, $utf8)
    Write-Host 'Geometry patched'
}

$planner = [IO.File]::ReadAllText((Resolve-Path $plannerPath), [Text.Encoding]::UTF8)
if ($planner -notmatch 'ABUTMENT_SURFACE_NONCONVEX_UNSUPPORTED') {
    Write-Host 'Planner already patched'
    exit 0
}

# Line-based rewrite of TryBuildSolidClippedSurface middle block
$lines = Get-Content -LiteralPath $plannerPath
$out = New-Object System.Collections.Generic.List[string]
$i = 0
while ($i -lt $lines.Count) {
    if ($lines[$i] -match 'var outer = loopPolygons\[0\];' -and ($i+1) -lt $lines.Count -and $lines[$i+1] -match 'ConvexHull') {
        # skip until seedSegments assignment ends (line with IncludeLast)
        $out.Add('        var outer = loopPolygons[0];')
        $out.Add('        if (outer.Count < 3 || Math.Abs(GeometryKernel.SignedArea(outer)) <= 1e-9)')
        $out.Add('        {')
        $out.Add('            error = "ABUTMENT_SURFACE_BOUNDARY_UNRESOLVED: outer loop khong hop le.";')
        $out.Add('            return false;')
        $out.Add('        }')
        $out.Add('')
        $out.Add('        var mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);')
        $out.Add('        var spacing = rule.SpacingMm * mm;')
        $out.Add('        if (spacing <= 1e-9)')
        $out.Add('        {')
        $out.Add('            error = "Surface spacing must be > 0.";')
        $out.Add('            return false;')
        $out.Add('        }')
        $out.Add('        var along = rule.GeometryTemplate is')
        $out.Add('            AbutmentGeometryTemplate.VerticalFront or')
        $out.Add('            AbutmentGeometryTemplate.VerticalBack')
        $out.Add('            ? new PlanPoint2(0, 1)')
        $out.Add('            : new PlanPoint2(1, 0);')
        $out.Add('        // True outer loop (convex or concave). Never substitute convex hull.')
        $out.Add('        var edgeInset = GeometryKernel.IsConvexPolygon(outer) ? centerlineOffset : 0d;')
        $out.Add('        var seedSegments = GeometryKernel.PlanPolygonMatLines(')
        $out.Add('            outer,')
        $out.Add('            along,')
        $out.Add('            edgeInset,')
        $out.Add('            spacing,')
        $out.Add('            rule.StartStationClearanceMm * mm,')
        $out.Add('            rule.EndStationClearanceMm * mm,')
        $out.Add('            rule.IncludeFirst,')
        $out.Add('            rule.IncludeLast);')
        # advance i past old block until after IncludeLast);
        while ($i -lt $lines.Count -and $lines[$i] -notmatch 'rule\.IncludeLast\)') { $i++ }
        $i++ # skip IncludeLast line
        if ($i -lt $lines.Count -and $lines[$i] -match '^\s*\);') { $i++ }
        continue
    }
    $out.Add($lines[$i])
    $i++
}
[IO.File]::WriteAllText((Resolve-Path $plannerPath), (($out -join "`r`n") + "`r`n"), $utf8)
Write-Host 'Planner patched'
if ((Get-Content $plannerPath -Raw) -match 'NONCONVEX_UNSUPPORTED') { throw 'old nonconvex error still present' }
if ((Get-Content $plannerPath -Raw) -notmatch 'PlanPolygonMatLines') { throw 'PlanPolygonMatLines not in planner' }
Write-Host 'OK'
