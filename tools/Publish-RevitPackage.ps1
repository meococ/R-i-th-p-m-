param(
    [ValidateSet('2025', '2026', '2027')]
    [string]$RevitVersion = '2025',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$project = (Resolve-Path (Join-Path $PSScriptRoot '..\BIM-DatViet.csproj')).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Output directory must be new so files from another Revit target cannot leak in: $output"
}

& dotnet restore $project "-p:RevitVersion=$RevitVersion"
if ($LASTEXITCODE -ne 0) { throw "Restore failed for Revit $RevitVersion." }

& dotnet publish $project -c Release "-p:RevitVersion=$RevitVersion" --no-restore -o $output
if ($LASTEXITCODE -ne 0) { throw "Publish failed for Revit $RevitVersion." }

$depsPath = Join-Path $output 'BIM.DatViet.deps.json'
$toolkitPath = Join-Path $output 'Nice3point.Revit.Toolkit.dll'
$extensionsPath = Join-Path $output 'Nice3point.Revit.Extensions.dll'
foreach ($path in @($depsPath, $toolkitPath, $extensionsPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Publish output is incomplete: $path" }
}

$deps = Get-Content -Raw -LiteralPath $depsPath | ConvertFrom-Json
$libraryNames = @($deps.libraries.PSObject.Properties.Name)
$expectedPrefix = "$RevitVersion."
foreach ($packageName in @('Nice3point.Revit.Toolkit', 'Nice3point.Revit.Extensions')) {
    $entry = $libraryNames | Where-Object { $_ -like "$packageName/*" } | Select-Object -First 1
    if (-not $entry) { throw "Missing $packageName from deps manifest." }
    $version = ($entry -split '/', 2)[1]
    if (-not $version.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        throw "$packageName target mismatch: expected $expectedPrefix*, got $version."
    }
}

foreach ($path in @($toolkitPath, $extensionsPath)) {
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
    if (-not $fileVersion.StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
        throw "Published dependency target mismatch: $path has FileVersion $fileVersion."
    }
}

[pscustomobject]@{
    RevitVersion = $RevitVersion
    OutputDirectory = $output
    MainDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'BIM.DatViet.dll')).Hash
    ToolkitFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($toolkitPath).FileVersion
    ExtensionsFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($extensionsPath).FileVersion
}
