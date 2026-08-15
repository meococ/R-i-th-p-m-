param(
    [long]$DiagnosticHostId = 3770128
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repo 'bin\Release.R25'
$srcManifest = Join-Path $repo 'BIM-DatViet.addin'
$safeRvt = Join-Path $repo 'tmp\review-workspace\SAFE_TEMP_REBAR.rvt'
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$addinManifest = Join-Path $addinRoot 'BIM.DatViet.addin'
$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$versionedName = "BIM.DatViet.R25.$stamp"
$targetDir = Join-Path $addinRoot $versionedName
$stagingRoot = Join-Path $repo ".deploy-staging\$versionedName"
$stagingDir = Join-Path $stagingRoot $versionedName
$backupRoot = Join-Path $repo "backups\r25-evidence-$stamp"

$packageFiles = @(
    'BIM.DatViet.dll',
    'BIM.DatViet.deps.json',
    'BIM.DatViet.runtimeconfig.json',
    'Nice3point.Revit.Extensions.dll',
    'Nice3point.Revit.Toolkit.dll',
    'CommunityToolkit.Mvvm.dll',
    'JetBrains.Annotations.dll'
)
foreach ($required in @($srcManifest, $safeRvt, $revitExe)) {
    if (-not (Test-Path $required)) { throw "Missing rollout artifact: $required" }
}
foreach ($relative in $packageFiles) {
    $path = Join-Path $buildDir $relative
    if (-not (Test-Path $path)) { throw "Missing R25 package file: $path" }
}
if (-not (Test-Path (Join-Path $buildDir 'Resources'))) {
    throw 'Missing R25 Resources directory.'
}
if (Test-Path $targetDir) { throw "Versioned target already exists: $targetDir" }

New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
foreach ($relative in $packageFiles) {
    Copy-Item (Join-Path $buildDir $relative) (Join-Path $stagingDir $relative) -Force
}
Copy-Item (Join-Path $buildDir 'Resources') (Join-Path $stagingDir 'Resources') -Recurse -Force

$srcDll = Join-Path $buildDir 'BIM.DatViet.dll'
$srcPreset = Join-Path $buildDir 'Resources\Presets\cau-van-cui-m2.v1.json'
$stagedDll = Join-Path $stagingDir 'BIM.DatViet.dll'
$stagedPreset = Join-Path $stagingDir 'Resources\Presets\cau-van-cui-m2.v1.json'
$srcDllHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash
$srcPresetHash = (Get-FileHash $srcPreset -Algorithm SHA256).Hash
if ($srcDllHash -ne (Get-FileHash $stagedDll -Algorithm SHA256).Hash) {
    throw 'Staged DLL checksum mismatch.'
}
if ($srcPresetHash -ne (Get-FileHash $stagedPreset -Algorithm SHA256).Hash) {
    throw 'Staged preset checksum mismatch.'
}

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
if (Test-Path $addinManifest) {
    Copy-Item $addinManifest (Join-Path $backupRoot 'BIM.DatViet.addin') -Force
}

Move-Item $stagingDir $targetDir
$manifestText = [IO.File]::ReadAllText($srcManifest)
$assemblyPath = "$versionedName\BIM.DatViet.dll"
$manifestText = [Text.RegularExpressions.Regex]::Replace(
    $manifestText,
    '<Assembly>.*?</Assembly>',
    "<Assembly>$assemblyPath</Assembly>")
$manifestTemp = Join-Path $addinRoot "BIM.DatViet.addin.$stamp.tmp"
[IO.File]::WriteAllText($manifestTemp, $manifestText, [Text.UTF8Encoding]::new($true))
Move-Item $manifestTemp $addinManifest -Force

$deployedDllHash = (Get-FileHash (Join-Path $targetDir 'BIM.DatViet.dll') -Algorithm SHA256).Hash
$deployedPresetHash = (Get-FileHash (
    Join-Path $targetDir 'Resources\Presets\cau-van-cui-m2.v1.json') -Algorithm SHA256).Hash
if ($srcDllHash -ne $deployedDllHash) { throw 'Deployed DLL checksum mismatch.' }
if ($srcPresetHash -ne $deployedPresetHash) { throw 'Deployed preset checksum mismatch.' }

$env:BIM_DATVIET_DIAGNOSTIC_HOST_ID = $DiagnosticHostId.ToString()
$safeArgument = [string]::Concat([char]34, $safeRvt, [char]34)
$process = Start-Process -FilePath $revitExe -ArgumentList $safeArgument -PassThru
Write-Host "BACKUP=$backupRoot"
Write-Host "DEPLOYED_DIR=$targetDir"
Write-Host "DLL_SHA256=$deployedDllHash"
Write-Host "PRESET_SHA256=$deployedPresetHash"
Write-Host "STARTED_PID=$($process.Id)"

$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 4
    $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($null -eq $running) { throw 'Revit exited while opening SAFE_TEMP_REBAR.rvt.' }
    if ($running.MainWindowTitle -match 'SAFE_TEMP_REBAR') {
        Write-Host "READY_TITLE=$($running.MainWindowTitle)"
        exit 0
    }
}
throw 'Revit started but SAFE_TEMP_REBAR.rvt was not observed within 240 seconds.'
