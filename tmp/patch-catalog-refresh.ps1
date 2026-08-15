$ErrorActionPreference = 'Stop'
$path = Join-Path (Get-Location) 'Controllers\AbutmentRebarController.cs'
$text = Get-Content -LiteralPath $path -Raw -Encoding UTF8

# Make AvailableBarTypeNames a live property
$oldProp = 'public IReadOnlyList<string> AvailableBarTypeNames { get; }'
$newProp = 'public IReadOnlyList<string> AvailableBarTypeNames => _barTypeCatalog.Names;'
if ($text -notlike "*$oldProp*") { throw 'AvailableBarTypeNames property not found' }
$text = $text.Replace($oldProp, $newProp)

# Remove assignment in ctor that is no longer needed (property is live)
$text = $text.Replace("        AvailableBarTypeNames = _barTypeCatalog.Names;`r`n", '')
$text = $text.Replace("        AvailableBarTypeNames = _barTypeCatalog.Names;`n", '')

# Insert refresh helper and call at start of Analyze
$helper = @'

    private void RefreshBarTypeCatalog()
    {
        _barTypeCatalog = new RebarTypeCatalog(_document);
        AlignPresetBarTypes(Preset);
        AlignPresetBarTypes(_basePreset);
    }
'@

if ($text -notmatch 'RefreshBarTypeCatalog') {
    $text = $text.Replace(
        "    private void AlignPresetBarTypes(AbutmentRebarPresetV1 preset)",
        $helper + "`r`n    private void AlignPresetBarTypes(AbutmentRebarPresetV1 preset)")
}

# Analyze should refresh catalog first
$oldAnalyze = @"
    public AbutmentRebarPlan? Analyze()
    {
        ReviewedSnapshot = null;
        AlignPresetBarTypes(Preset);
        Preset.RuleHash = Preset.ComputeHash();
"@
$newAnalyze = @"
    public AbutmentRebarPlan? Analyze()
    {
        ReviewedSnapshot = null;
        // Rebuild catalog each Analyze so Transfer Project Standards mid-session is visible.
        RefreshBarTypeCatalog();
        Preset.RuleHash = Preset.ComputeHash();
"@
if ($text -notlike "*$($oldAnalyze.Substring(0,40))*") {
    # try LF only
    $oldAnalyze = $oldAnalyze -replace "`r`n", "`n"
    $newAnalyze = $newAnalyze -replace "`r`n", "`n"
}
if ($text.Contains("RefreshBarTypeCatalog();\r\n        Preset.RuleHash") -or $text.Contains("RefreshBarTypeCatalog();\n        Preset.RuleHash")) {
    Write-Host 'Analyze already refreshes'
} else {
    if (-not $text.Contains('AlignPresetBarTypes(Preset);')) { throw 'Analyze align call missing' }
    # only first Analyze occurrence after method start
    $idx = $text.IndexOf('public AbutmentRebarPlan? Analyze()')
    if ($idx -lt 0) { throw 'Analyze not found' }
    $chunk = $text.Substring($idx, 400)
    if ($chunk -notmatch 'AlignPresetBarTypes\(Preset\);') { throw "Analyze chunk unexpected: $chunk" }
    $text2 = $text.Substring(0, $idx) + $chunk.Replace('AlignPresetBarTypes(Preset);', "RefreshBarTypeCatalog();`r`n        AbutmentDiagnostics.LogMessage(`"REBAR_BAR_TYPE_CATALOG`", `_barTypeCatalog.IsEmpty ? `"EMPTY`" : string.Join(`", `", AvailableBarTypeNames));") + $text.Substring($idx + 400)
    # The above is fragile. Do simpler line-based.
    $text = $null
}

# Safer line-based rewrite
$lines = Get-Content -LiteralPath $path -Encoding UTF8
$out = New-Object System.Collections.Generic.List[string]
$i = 0
$insertedHelper = $false
while ($i -lt $lines.Count) {
    $line = $lines[$i]

    if ($line -match '^\s*public IReadOnlyList<string> AvailableBarTypeNames') {
        $out.Add('    public IReadOnlyList<string> AvailableBarTypeNames => _barTypeCatalog.Names;')
        $i++; continue
    }
    if ($line -match 'AvailableBarTypeNames = _barTypeCatalog\.Names;') {
        $i++; continue
    }
    if ($line -match 'public AbutmentRebarPlan\? Analyze\(\)') {
        $out.Add($line); $i++
        # copy until AlignPresetBarTypes(Preset);
        while ($i -lt $lines.Count) {
            $l2 = $lines[$i]
            if ($l2 -match 'AlignPresetBarTypes\(Preset\);') {
                $out.Add('        // Rebuild catalog each Analyze so Transfer Project Standards mid-session is visible.')
                $out.Add('        RefreshBarTypeCatalog();')
                $out.Add('        AbutmentDiagnostics.LogMessage(')
                $out.Add('            "REBAR_BAR_TYPE_CATALOG",')
                $out.Add('            _barTypeCatalog.IsEmpty')
                $out.Add('                ? "EMPTY"')
                $out.Add('                : ("count=" + _barTypeCatalog.Count + " | " + string.Join(", ", AvailableBarTypeNames)));')
                $i++
                break
            }
            $out.Add($l2); $i++
        }
        continue
    }
    if (-not $insertedHelper -and $line -match 'private void AlignPresetBarTypes\(') {
        $out.Add('    private void RefreshBarTypeCatalog()')
        $out.Add('    {')
        $out.Add('        _barTypeCatalog = new RebarTypeCatalog(_document);')
        $out.Add('        AlignPresetBarTypes(Preset);')
        $out.Add('        AlignPresetBarTypes(_basePreset);')
        $out.Add('    }')
        $out.Add('')
        $insertedHelper = $true
    }
    $out.Add($line)
    $i++
}

# field must be non-readonly to reassign
$final = ($out -join "`r`n")
$final = $final.Replace('private readonly RebarTypeCatalog _barTypeCatalog;', 'private RebarTypeCatalog _barTypeCatalog;')
[System.IO.File]::WriteAllText($path, $final + "`r`n", [System.Text.UTF8Encoding]::new($false))
Write-Host 'PATCHED controller'
Select-String -Path $path -Pattern 'RefreshBarTypeCatalog|AvailableBarTypeNames|REBAR_BAR_TYPE_CATALOG' |
    ForEach-Object { "{0}:{1}" -f $_.LineNumber, $_.Line.Trim() }
