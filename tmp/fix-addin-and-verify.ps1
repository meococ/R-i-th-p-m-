$ErrorActionPreference = 'Stop'
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$bad = Join-Path $addinRoot 'BIM-DatViet.addin'
$good = Join-Path $addinRoot 'BIM.DatViet.addin'
$surfaceDir = Join-Path $addinRoot 'BIM.DatViet.SurfaceFace.20260807'

Write-Host "GOOD=$good exists=$(Test-Path $good)"
Write-Host "BAD=$bad exists=$(Test-Path $bad)"
Write-Host "SURFACE_DIR exists=$(Test-Path $surfaceDir)"

if (Test-Path $bad) {
    $content = Get-Content $bad -Raw
    if ($content -match 'SurfaceFace') {
        $bak = Join-Path $addinRoot ("BIM-DatViet.addin.bak-" + (Get-Date -Format 'yyyyMMddTHHmmss'))
        Move-Item $bad $bak -Force
        Write-Host "MOVED_BAD_TO=$bak"
    } else {
        Write-Host 'BAD_ADDIN_NOT_SURFACE_KEEP'
    }
}

# Ensure good addin content
@'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BIM-DatViet</Name>
    <Assembly>BIM.DatViet\BIM.DatViet.dll</Assembly>
    <AddInId>6F1772D7-34FD-427B-B494-48231A67D9D3</AddInId>
    <FullClassName>BIM.DatViet.Application</FullClassName>
    <VendorId>Development</VendorId>
    <VendorDescription></VendorDescription>
  </AddIn>
</RevitAddIns>
'@ | Set-Content -Path $good -Encoding UTF8
Write-Host 'WROTE_GOOD_ADDIN'

Get-ChildItem $addinRoot -Filter '*.addin' | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

# Current Revit is already running with potentially dual registration.
# Restart once so only the good addin loads.
$procs = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    Write-Host "Restarting Revit PIDs=$($procs.Id -join ',')"
    foreach ($p in $procs) {
        try { $null = $p.CloseMainWindow() } catch {}
    }
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Seconds 2
    }
    $alive = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
    if ($alive.Count -gt 0) {
        $alive | Stop-Process -Force
        Start-Sleep -Seconds 3
    }
}

$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
# Resolve SAFE path relative to script location parent
$repo = Split-Path -Parent $PSScriptRoot
$safe = Join-Path $repo 'tmp\review-workspace\SAFE_TEMP_REBAR.rvt'
if (-not (Test-Path $safe)) { throw "SAFE missing: $safe" }

Write-Host "Launch $safe"
$proc = Start-Process -FilePath $revitExe -ArgumentList @('"' + $safe + '"') -PassThru
Write-Host "PID=$($proc.Id)"

$ready = $false
for ($i=1; $i -le 36; $i++) {
    Start-Sleep -Seconds 5
    $rp = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $rp) { throw 'Revit exited' }
    $title = $rp.MainWindowTitle
    Write-Host "T=$i TITLE='$title' WS=$([math]::Round($rp.WorkingSet64/1MB,1))MB"
    if ($title -match 'SAFE_TEMP_REBAR') { $ready = $true; break }
}
Write-Host "READY=$ready"

# Journal check for addin load
Start-Sleep -Seconds 5
$journalRoot = Join-Path $env:LOCALAPPDATA 'Autodesk\Revit\Autodesk Revit 2025\Journals'
$latest = Get-ChildItem $journalRoot -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "JOURNAL=$($latest.FullName)"
Select-String -Path $latest.FullName -Pattern 'BIM\.DatViet|SurfaceFace|AddIn|Exception|failed to|vendor|DVB_ADDIN|Rải|Abutment' |
    Select-Object -First 60 |
    ForEach-Object { $_.Line.Trim() }
