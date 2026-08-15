$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$preset = Join-Path $root 'Resources\Presets\cau-van-cui-m2.v1.json'
$stamp = Get-Date -Format 'yyyyMMddTHHmmss'
$backup = "$preset.$stamp.bak"
Copy-Item -LiteralPath $preset -Destination $backup -Force
Write-Output "BACKUP  -> $backup"
Write-Output ("SHA256  -> " + (Get-FileHash -Algorithm SHA256 -LiteralPath $preset).Hash)
