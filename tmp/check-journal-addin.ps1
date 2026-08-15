$ErrorActionPreference = 'Continue'
$journalRoot = Join-Path $env:LOCALAPPDATA 'Autodesk\Revit\Autodesk Revit 2025\Journals'
$latest = Get-ChildItem $journalRoot -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "JOURNAL=$($latest.FullName) LEN=$($latest.Length) TIME=$($latest.LastWriteTime)"

$patterns = @(
    'BIM.DatViet',
    'BIM-DatViet',
    'SurfaceFace',
    '6F1772D7',
    '6f1772d7',
    'DVB_ADDIN',
    'Rải Thép',
    'AbutmentRebar',
    'ExternalApplication',
    'OnStartup',
    'AddIn',
    'addin',
    'Exception',
    'Failed to load',
    'failed to initialize'
)

foreach ($pat in $patterns) {
    $hits = @(Select-String -Path $latest.FullName -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue)
    Write-Host ("PAT={0} COUNT={1}" -f $pat, $hits.Count)
    $hits | Select-Object -First 8 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }
}

Write-Host '--- process ---'
Get-Process -Name Revit -ErrorAction SilentlyContinue |
    Select-Object Id, MainWindowTitle, Responding, @{N='WS_MB';E={[math]::Round($_.WorkingSet64/1MB,1)}} |
    Format-List

Write-Host '--- addins ---'
Get-ChildItem (Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025') -Filter '*.addin' |
    Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

$dll = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\BIM.DatViet\BIM.DatViet.dll'
$preset = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\BIM.DatViet\Resources\Presets\cau-van-cui-m2.v1.json'
Get-Item $dll, $preset | Select-Object FullName, Length, LastWriteTime | Format-List
Select-String -Path $preset -Pattern 'ruleVersion|requiredZones|\"layerOrder\"' |
    Select-Object -First 12 |
    ForEach-Object { $_.Line.Trim() }
