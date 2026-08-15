$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$parent = Split-Path -Parent $repo
$tests = Join-Path $parent 'BIM-DatViet.Tests\BIM-DatViet.Tests.csproj'
dotnet test $tests -c Release --nologo --filter "FullyQualifiedName~AbutmentFootingBandKernel|FullyQualifiedName~AbutmentFootingDepthIndependence|FullyQualifiedName~PresetWithTheSpliceBlocks|FullyQualifiedName~AbutmentKernelTests.LoadedPreset"
Write-Host "TEST_EXIT=$LASTEXITCODE"
