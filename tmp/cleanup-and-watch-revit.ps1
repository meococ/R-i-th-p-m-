$ErrorActionPreference = 'Continue'
$addinDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\BIM.DatViet'
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'

# Remove stale preset at package root (canonical path is Resources\Presets\)
$stalePreset = Join-Path $addinDir 'cau-van-cui-m2.v1.json'
if (Test-Path $stalePreset) {
    Remove-Item $stalePreset -Force
    Write-Host "REMOVED_STALE_PRESET=$stalePreset"
} else {
    Write-Host 'NO_STALE_PRESET'
}

# Prefer single manifest BIM.DatViet.addin; if both exist keep both only if identical assembly path
$a = Join-Path $addinRoot 'BIM.DatViet.addin'
$b = Join-Path $addinRoot 'BIM-DatViet.addin'
if ((Test-Path $a) -and (Test-Path $b)) {
    $ta = (Get-Content $a -Raw)
    $tb = (Get-Content $b -Raw)
    if ($ta -eq $tb -or ($tb -match 'BIM\.DatViet\\BIM\.DatViet\.dll' -and $ta -match 'BIM\.DatViet\\BIM\.DatViet\.dll')) {
        # Duplicate registration risk: keep BIM.DatViet.addin, remove hyphen variant
        Remove-Item $b -Force
        Write-Host "REMOVED_DUP_ADDIN=$b"
    } else {
        Write-Host 'KEEP_BOTH_ADDINS_DIFFERENT'
        Write-Host '--- BIM.DatViet.addin ---'; Write-Host $ta
        Write-Host '--- BIM-DatViet.addin ---'; Write-Host $tb
    }
}

$goodPreset = Join-Path $addinDir 'Resources\Presets\cau-van-cui-m2.v1.json'
Write-Host "GOOD_PRESET_EXISTS=$(Test-Path $goodPreset)"
if (Test-Path $goodPreset) {
    Select-String -Path $goodPreset -Pattern 'ruleVersion|requiredZones' | ForEach-Object { $_.Line.Trim() }
}

# Watch Revit title up to ~2 minutes
for ($i = 1; $i -le 24; $i++) {
    Start-Sleep -Seconds 5
    $p = Get-Process -Name Revit -ErrorAction SilentlyContinue
    if ($null -eq $p) {
        Write-Host "T=${i} REVIT_GONE"
        break
    }
    $title = $p.MainWindowTitle
    $ws = [math]::Round($p.WorkingSet64/1MB,1)
    Write-Host "T=${i} PID=$($p.Id) WS=${ws}MB TITLE='$title' RESP=$($p.Responding)"
    if ($title -and $title -notmatch 'Starting|Initializing') {
        Write-Host 'WINDOW_READY'
        break
    }
}

# Latest journal snippets
$journals = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Autodesk\Revit') -Recurse -Filter 'journal.*.txt' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3
foreach ($j in $journals) {
    Write-Host "JOURNAL=$($j.FullName) time=$($j.LastWriteTime)"
}
if ($journals) {
    $latest = $journals[0].FullName
    Write-Host '--- journal tail interesting lines ---'
    Select-String -Path $latest -Pattern 'BIM\.DatViet|BIM-DatViet|AddIn|Exception|Error|SAFE_TEMP|failed|ribbon|DVB' -SimpleMatch:$false |
        Select-Object -Last 40 |
        ForEach-Object { $_.Line }
}
