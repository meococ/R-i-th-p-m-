$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repo 'bin\Release.R25'
$srcDll = Join-Path $buildDir 'BIM.DatViet.dll'
$srcPreset = Join-Path $buildDir 'Resources\Presets\cau-van-cui-m2.v1.json'
$srcManifest = Join-Path $repo 'BIM-DatViet.addin'
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$addinDir = Join-Path $addinRoot 'BIM.DatViet'
$addinManifest = Join-Path $addinRoot 'BIM.DatViet.addin'
$safe = Join-Path $repo 'tmp\review-workspace\SAFE_TEMP_REBAR.rvt'
$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$backupRoot = Join-Path $repo "backups\r25-integrity-$stamp"
$stagingRoot = Join-Path $repo ".deploy-staging\r25-integrity-$stamp"
$stagingDir = Join-Path $stagingRoot 'BIM.DatViet'

foreach ($required in @($srcDll, $srcPreset, $srcManifest, $safe, $revitExe)) {
    if (-not (Test-Path $required)) { throw "Missing required rollout artifact: $required" }
}

$packageFiles = @(
    'BIM.DatViet.dll',
    'BIM.DatViet.deps.json',
    'BIM.DatViet.runtimeconfig.json',
    'Nice3point.Revit.Extensions.dll',
    'Nice3point.Revit.Toolkit.dll',
    'CommunityToolkit.Mvvm.dll',
    'JetBrains.Annotations.dll'
)
foreach ($relative in $packageFiles) {
    if (-not (Test-Path (Join-Path $buildDir $relative))) {
        throw "Missing R25 package file: $relative"
    }
}

New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
foreach ($relative in $packageFiles) {
    Copy-Item (Join-Path $buildDir $relative) (Join-Path $stagingDir $relative) -Force
}
Copy-Item (Join-Path $buildDir 'Resources') (Join-Path $stagingDir 'Resources') -Recurse -Force
Copy-Item $srcManifest (Join-Path $stagingRoot 'BIM.DatViet.addin') -Force

$sourcePresetHash = (Get-FileHash $srcPreset -Algorithm SHA256).Hash
$stagedPresetHash = (Get-FileHash (Join-Path $stagingDir 'Resources\Presets\cau-van-cui-m2.v1.json') -Algorithm SHA256).Hash
if ($sourcePresetHash -ne $stagedPresetHash) { throw 'Staged preset checksum mismatch.' }

$procs = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    Write-Host "Requesting graceful Revit close: $($procs.Id -join ',')"
    foreach ($process in $procs) { $null = $process.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and
           @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Seconds 2
    }
    $alive = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
    if ($alive.Count -gt 0) {
        throw "Revit did not close cleanly; rollout aborted without force-kill or file changes. PID: $($alive.Id -join ',')"
    }
}

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
if (Test-Path $addinDir) {
    Copy-Item $addinDir (Join-Path $backupRoot 'BIM.DatViet') -Recurse -Force
}
if (Test-Path $addinManifest) {
    Copy-Item $addinManifest (Join-Path $backupRoot 'BIM.DatViet.addin') -Force
}

$oldDir = Join-Path $backupRoot 'BIM.DatViet.previous'
if (Test-Path $addinDir) { Move-Item $addinDir $oldDir }
try {
    Move-Item $stagingDir $addinDir
    Copy-Item (Join-Path $stagingRoot 'BIM.DatViet.addin') $addinManifest -Force
}
catch {
    if (-not (Test-Path $addinDir) -and (Test-Path $oldDir)) {
        Move-Item $oldDir $addinDir
    }
    throw
}

$deployedDll = Join-Path $addinDir 'BIM.DatViet.dll'
$deployedPreset = Join-Path $addinDir 'Resources\Presets\cau-van-cui-m2.v1.json'
$sourceDllHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash
$deployedDllHash = (Get-FileHash $deployedDll -Algorithm SHA256).Hash
$deployedPresetHash = (Get-FileHash $deployedPreset -Algorithm SHA256).Hash
if ($sourceDllHash -ne $deployedDllHash) { throw 'Deployed DLL checksum mismatch.' }
if ($sourcePresetHash -ne $deployedPresetHash) { throw 'Deployed preset checksum mismatch.' }

Write-Host "BACKUP=$backupRoot"
Write-Host "DLL_SHA256=$deployedDllHash"
Write-Host "PRESET_SHA256=$deployedPresetHash"

$proc = Start-Process -FilePath $revitExe -ArgumentList @('"' + $safe + '"') -PassThru
Write-Host "STARTED_PID=$($proc.Id)"
$readyDeadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $readyDeadline) {
    Start-Sleep -Seconds 4
    $running = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $running) { throw 'Revit exited while opening SAFE_TEMP_REBAR.rvt.' }
    if ($running.MainWindowTitle -match 'SAFE_TEMP_REBAR') {
        Write-Host "READY_TITLE=$($running.MainWindowTitle)"
        exit 0
    }
}
throw 'Revit started but SAFE_TEMP_REBAR.rvt was not observed within 240 seconds.'
