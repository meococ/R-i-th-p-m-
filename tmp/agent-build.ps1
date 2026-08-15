$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $root
dotnet build 'BIM-DatViet.csproj' -c Release.R25 --nologo -p:DeployAddin=false -p:LaunchRevit=false
Write-Host "BUILD_EXIT=$LASTEXITCODE"
Pop-Location
