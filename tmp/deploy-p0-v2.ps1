$ErrorActionPreference = 'Stop'
$src = (Resolve-Path 'bin/Release.R25').Path
$packageRoot = '.deploy-staging/BIM.DatViet.P0.R25.20260811.8'
$packageBin = Join-Path $packageRoot 'BIM.DatViet'
$runtimeBin = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/BIM.DatViet.R25.20260811T111718Z'
New-Item -ItemType Directory -Force -Path $packageBin, $runtimeBin | Out-Null
$files = @(
    'BIM.DatViet.dll',
    'BIM.DatViet.deps.json',
    'BIM.DatViet.runtimeconfig.json',
    'Nice3point.Revit.Extensions.dll',
    'Nice3point.Revit.Toolkit.dll',
    'CommunityToolkit.Mvvm.dll'
)
foreach ($file in $files) {
    Copy-Item -Force (Join-Path $src $file) (Join-Path $packageBin $file)
    Copy-Item -Force (Join-Path $src $file) (Join-Path $runtimeBin $file)
}
Copy-Item -Recurse -Force (Join-Path $src 'Resources') $packageBin
Copy-Item -Recurse -Force (Join-Path $src 'Resources') $runtimeBin
Write-Output "PACKAGE=$packageRoot"
Write-Output "RUNTIME=$runtimeBin"
