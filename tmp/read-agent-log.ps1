$ErrorActionPreference = 'Continue'

# newest runtime log
$root = Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent\runtimes'
$logs = Get-ChildItem $root -Recurse -Filter '*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending
Write-Host "LOG_COUNT=$($logs.Count)"
$latest = $logs | Select-Object -First 1
if ($latest) {
    Write-Host "LATEST=$($latest.FullName) TIME=$($latest.LastWriteTime) LEN=$($latest.Length)"
    Get-Content -LiteralPath $latest.FullName -Tail 120
}

Write-Host '=== find rvt ==='
$base = 'C:\Users\ADMIN\Downloads\01. Cong Viec DV'
Get-ChildItem -LiteralPath $base -Recurse -Filter '*CAU VAN CUI*ver 25*.rvt' -ErrorAction SilentlyContinue |
    Select-Object -First 10 FullName, Length, LastWriteTime |
    Format-List

Get-ChildItem -LiteralPath $base -Recurse -Filter '*070626*.rvt' -ErrorAction SilentlyContinue |
    Select-Object -First 10 FullName, Length, LastWriteTime |
    Format-List
