$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repo '.deploy-staging'
$package = 'BIM.DatViet.ProfileSolidGate.R25.20260814.1'
$publishOut = Join-Path $staging (Join-Path $package 'BIM.DatViet')
$publishScript = Join-Path $repo 'tools\Publish-RevitPackage.ps1'
$deployScript = Join-Path $staging '_deploy-r25.ps1'

$revit = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($revit.Count -gt 0) {
    Write-Output ("REVIT_RUNNING=1 pid=" + (($revit | ForEach-Object { $_.Id }) -join ','))
    Write-Output 'Khong tat Revit. Deploy side-by-side vao thu muc moi; Owner dong/mo lai Revit de nap ban nay.'
}

if (Test-Path -LiteralPath $publishOut) {
    Remove-Item -LiteralPath $publishOut -Recurse -Force
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $publishScript -RevitVersion 2025 -OutputDirectory $publishOut
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }

& powershell -NoProfile -ExecutionPolicy Bypass -File $deployScript
if ($LASTEXITCODE -ne 0) { throw "Deploy failed: $LASTEXITCODE" }
