param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('2025', '2026')]
    [string]$RevitVersion
)

$ErrorActionPreference = 'Stop'
$targetLabel = if ($RevitVersion -eq '2025') { 'R25' } else { 'R26' }
$revitExecutable = "*\Revit $RevitVersion\Revit.exe"

$runningHost = Get-Process Revit -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like $revitExecutable } catch { $false }
}
if ($runningHost) {
    throw "Revit $RevitVersion is running; deployment was not started."
}

$addinRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"))
$targetDirectory = [IO.Path]::GetFullPath((Join-Path $addinRoot 'BIM.DatViet'))
$targetManifest = [IO.Path]::GetFullPath((Join-Path $addinRoot 'BIM-DatViet.addin'))
$allowedPrefix = $addinRoot.TrimEnd('\') + '\'
if (-not $targetDirectory.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $targetManifest.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved deployment targets are outside the exact Revit add-in directory.'
}

$scriptRoot = Split-Path -Parent $PSCommandPath
$stageDirectory = [IO.Path]::GetFullPath(
    (Join-Path $scriptRoot "mvvm-stackfix-20260803\$targetLabel"))
$sourceManifest = [IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $scriptRoot) 'BIM-DatViet.addin'))
if (-not (Test-Path -LiteralPath $stageDirectory) -or
    -not (Test-Path -LiteralPath $sourceManifest)) {
    throw 'Validated staging payload or source manifest is missing.'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "BIM-DatViet\deploy-backups\$timestamp-mvvm-stackfix-$targetLabel"))
$backupDirectory = Join-Path $backupRoot 'BIM.DatViet'
$backupManifest = Join-Path $backupRoot 'BIM-DatViet.addin'
$failedPayload = Join-Path $backupRoot 'failed-new-payload'

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$movedDirectory = $false
$movedManifest = $false

try {
    if (Test-Path -LiteralPath $targetDirectory) {
        Move-Item -LiteralPath $targetDirectory -Destination $backupDirectory
        $movedDirectory = $true
    }
    if (Test-Path -LiteralPath $targetManifest) {
        Move-Item -LiteralPath $targetManifest -Destination $backupManifest
        $movedManifest = $true
    }

    Copy-Item -LiteralPath $stageDirectory -Destination $targetDirectory -Recurse
    Copy-Item -LiteralPath $sourceManifest -Destination $targetManifest

    $stageFiles = @(Get-ChildItem -LiteralPath $stageDirectory -Recurse -File)
    $liveFiles = @(Get-ChildItem -LiteralPath $targetDirectory -Recurse -File)
    if ($stageFiles.Count -ne $liveFiles.Count) {
        throw "File count mismatch: stage=$($stageFiles.Count), live=$($liveFiles.Count)."
    }

    foreach ($sourceFile in $stageFiles) {
        $relativePath = $sourceFile.FullName.Substring($stageDirectory.Length + 1)
        $liveFile = Join-Path $targetDirectory $relativePath
        if (-not (Test-Path -LiteralPath $liveFile)) {
            throw "Missing live file: $relativePath"
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $liveHash = (Get-FileHash -LiteralPath $liveFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $liveHash) {
            throw "Hash mismatch: $relativePath"
        }
    }

    [xml]$manifestXml = Get-Content -LiteralPath $targetManifest -Raw
    if (@($manifestXml.RevitAddIns.AddIn).Count -ne 1 -or
        $null -ne $manifestXml.RevitAddIns.ManifestSettings) {
        throw 'Manifest must contain exactly one AddIn and no unsupported ManifestSettings node.'
    }
    $assembly = $manifestXml.RevitAddIns.AddIn.Assembly
    if ($assembly -ne 'BIM.DatViet\BIM.DatViet.dll') {
        throw "Manifest assembly mismatch: $assembly"
    }

    $presetPath = Join-Path $targetDirectory 'Resources\Presets\cau-van-cui-m2.v1.json'
    $preset = Get-Content -LiteralPath $presetPath -Raw | ConvertFrom-Json
    $dllPath = Join-Path $targetDirectory 'BIM.DatViet.dll'
    [PSCustomObject]@{
        Version = $RevitVersion
        Backup = $backupRoot
        FileCount = $liveFiles.Count
        DllSha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
        RuleVersion = $preset.ruleVersion
        ManifestAssembly = $assembly
        Status = 'DEPLOYED_AND_VERIFIED'
    }
}
catch {
    if (Test-Path -LiteralPath $targetDirectory) {
        Move-Item -LiteralPath $targetDirectory -Destination $failedPayload
    }
    if (Test-Path -LiteralPath $targetManifest) {
        Move-Item -LiteralPath $targetManifest -Destination (Join-Path $backupRoot 'failed-new-manifest.addin')
    }
    if ($movedDirectory -and (Test-Path -LiteralPath $backupDirectory)) {
        Move-Item -LiteralPath $backupDirectory -Destination $targetDirectory
    }
    if ($movedManifest -and (Test-Path -LiteralPath $backupManifest)) {
        Move-Item -LiteralPath $backupManifest -Destination $targetManifest
    }
    throw
}
