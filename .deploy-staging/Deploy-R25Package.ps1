<#
.SYNOPSIS
    Publish + deploy mot goi BIM-DatViet cho Revit 2025 theo kieu side-by-side.

.DESCRIPTION
    Thay cho loat script deploy-*.ps1 gan trung nhau: ten goi va mo ta la tham so,
    khong hardcode. Deploy side-by-side vao thu muc rieng nen KHONG can tat Revit;
    Owner dong/mo lai Revit de nap ban moi.

.EXAMPLE
    .\Deploy-R25Package.ps1 -PackageName 'BIM.DatViet.BandStarter.R25.20260814.2' -Description 'Band kernel + thep cho'
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageName,

    [Parameter(Mandatory = $true)]
    [string]$Description
)

$ErrorActionPreference = 'Stop'

$staging = $PSScriptRoot
$repo = Split-Path -Parent $staging
$source = Join-Path $staging (Join-Path $PackageName 'BIM.DatViet')
$addinRoot = Join-Path $env:AppData 'Autodesk\Revit\Addins\2025'
$targetDir = Join-Path $addinRoot (Join-Path $PackageName 'BIM.DatViet')
$manifest = Join-Path $addinRoot 'BIM.DatViet.addin'
$stamp = Get-Date -Format 'yyyyMMddTHHmmss'

$revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($revit.Count -gt 0) {
    Write-Output ('REVIT_RUNNING=1 pid=' + (($revit | ForEach-Object { $_.Id }) -join ','))
    Write-Output 'Deploy side-by-side vao thu muc moi; Owner dong/mo lai Revit de nap ban nay.'
}

# --- Publish ---
if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $repo 'tools\Publish-RevitPackage.ps1') `
    -RevitVersion 2025 -OutputDirectory $source
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $source)) { throw "Khong tim thay goi vua build: $source" }

# --- Deploy ---
if (Test-Path -LiteralPath $manifest) {
    Copy-Item -LiteralPath $manifest -Destination "$manifest.bak-$stamp" -Force
    Write-Output "BACKUP manifest cu -> $manifest.bak-$stamp"
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
    <Assembly>$PackageName\BIM.DatViet\BIM.DatViet.dll</Assembly>
    <AddInId>6F1772D7-34FD-427B-B494-48231A67D9D3</AddInId>
    <FullClassName>BIM.DatViet.Application</FullClassName>
    <VendorId>Development</VendorId>
    <VendorDescription>$Description</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -LiteralPath $manifest -Value $xml -Encoding UTF8

# --- Doi chieu tai nguyen nguon vs deploy ---
Write-Output ''
Write-Output '--- KET QUA ---'
Write-Output "DLL:        $dll"
Write-Output "DLL SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash)"
Write-Output "Manifest:   $manifest"

$pairs = @(
    @('Resources\Presets\cau-van-cui-m2.v1.json', 'PRESET      '),
    @('Resources\DVB_Abutment_SharedParameters.txt', 'SHARED PARAM')
)
foreach ($pair in $pairs) {
    $src = Join-Path $repo $pair[0]
    $dst = Join-Path $targetDir $pair[0]
    $label = $pair[1]
    if (-not (Test-Path -LiteralPath $dst)) { Write-Output "$label : THIEU trong goi deploy"; continue }
    $h1 = (Get-FileHash -Algorithm SHA256 -LiteralPath $src).Hash
    $h2 = (Get-FileHash -Algorithm SHA256 -LiteralPath $dst).Hash
    if ($h1 -eq $h2) { Write-Output "$label : KHOP  $h2" }
    else { Write-Output "$label : LECH!  nguon=$h1  deploy=$h2" }
}

Write-Output ''
Write-Output '--- CAC MANIFEST DANG BAT CHO REVIT 2025 ---'
Get-ChildItem -LiteralPath $addinRoot -Filter '*.addin' | ForEach-Object { Write-Output "  $($_.Name)" }
