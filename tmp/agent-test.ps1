$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$parent = Split-Path -Parent $root
$tests = Join-Path $parent 'BIM-DatViet.Tests\BIM-DatViet.Tests.csproj'
dotnet test $tests -c Release --nologo
Write-Host "TEST_EXIT=$LASTEXITCODE"
