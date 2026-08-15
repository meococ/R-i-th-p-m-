$ErrorActionPreference = 'Stop'

# Script nam trong <repo>\.deploy-staging nen suy nguoc ra repo, tranh hardcode duong dan co dau.
$staging = $PSScriptRoot
$repo = Split-Path -Parent $staging
$package = 'BIM.DatViet.ProfileSolidGate.R25.20260814.1'
$source = Join-Path $staging (Join-Path $package 'BIM.DatViet')
$addinRoot = Join-Path $env:AppData 'Autodesk\Revit\Addins\2025'
$targetDir = Join-Path $addinRoot (Join-Path $package 'BIM.DatViet')
$manifest = Join-Path $addinRoot 'BIM.DatViet.addin'
$stamp = Get-Date -Format 'yyyyMMddTHHmmss'

if (-not (Test-Path -LiteralPath $source)) { throw "Khong tim thay goi vua build: $source" }

if (Test-Path -LiteralPath $manifest) {
    $backup = "$manifest.bak-$stamp"
    Copy-Item -LiteralPath $manifest -Destination $backup -Force
    Write-Output "BACKUP manifest cu -> $backup"
}

if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $targetDir -Recurse -Force

$dll = Join-Path $targetDir 'BIM.DatViet.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Chep thieu DLL: $dll" }

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BIM-DatViet</Name>
    <Assembly>$package\BIM.DatViet\BIM.DatViet.dll</Assembly>
    <AddInId>6F1772D7-34FD-427B-B494-48231A67D9D3</AddInId>
    <FullClassName>BIM.DatViet.Application</FullClassName>
    <VendorId>Development</VendorId>
    <VendorDescription>Profile-solid gate: depth mismatch warns, plan 8mm stays fail-closed - 2026-08-14.1</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -LiteralPath $manifest -Value $xml -Encoding UTF8

$presetDeployed = Join-Path $targetDir 'Resources\Presets\cau-van-cui-m2.v1.json'
$presetSource = Join-Path $repo 'Resources\Presets\cau-van-cui-m2.v1.json'
$sharedDeployed = Join-Path $targetDir 'Resources\DVB_Abutment_SharedParameters.txt'
$sharedSource = Join-Path $repo 'Resources\DVB_Abutment_SharedParameters.txt'

Write-Output ''
Write-Output '--- KET QUA ---'
Write-Output "DLL:        $dll"
Write-Output "DLL SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash)"
Write-Output "Manifest:   $manifest"

foreach ($pair in @(@($presetSource, $presetDeployed, 'PRESET      '), @($sharedSource, $sharedDeployed, 'SHARED PARAM'))) {
    $src = $pair[0]; $dst = $pair[1]; $label = $pair[2]
    if (-not (Test-Path -LiteralPath $dst)) { Write-Output "$label : THIEU trong goi deploy"; continue }
    $h1 = (Get-FileHash -Algorithm SHA256 -LiteralPath $src).Hash
    $h2 = (Get-FileHash -Algorithm SHA256 -LiteralPath $dst).Hash
    if ($h1 -eq $h2) { Write-Output "$label : KHOP  $h2" } else { Write-Output "$label : LECH!  nguon=$h1  deploy=$h2" }
}

Write-Output ''
Write-Output '--- CAC MANIFEST DANG BAT CHO REVIT 2025 ---'
Get-ChildItem -LiteralPath $addinRoot -Filter '*.addin' | ForEach-Object { Write-Output "  $($_.Name)" }
