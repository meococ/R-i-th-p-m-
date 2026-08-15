$ErrorActionPreference = 'Continue'

Write-Host 'Stopping BIM765T.Revit.McpHost...'
Get-Process -Name 'BIM765T.Revit.McpHost' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("Kill PID={0}" -f $_.Id)
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Seconds 2

# Find host binary near agent shadow or common install paths
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent\tools\BIM765T.Revit.McpHost.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\BIM765T\BIM765T.Revit.McpHost.exe'),
    (Join-Path $env:LOCALAPPDATA 'BIM765T\BIM765T.Revit.McpHost.exe')
)
Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'BIM765T*') -Recurse -Filter 'BIM765T.Revit.McpHost.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 10 FullName, LastWriteTime |
    ForEach-Object { Write-Host ("FOUND {0}" -f $_.FullName); $candidates += $_.FullName }

$exe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
Write-Host "EXE=$exe"
if (-not $exe) {
    Write-Host 'NO_MCP_HOST_EXE'
    exit 2
}

Write-Host 'Starting MCP host...'
$p = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
Write-Host ("STARTED_PID={0}" -f $p.Id)
Start-Sleep -Seconds 5
Get-Process -Name 'BIM765T.Revit.McpHost' -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, Responding | Format-Table -AutoSize

# Revit still needs agent registration; check newest log tail after a few seconds
Start-Sleep -Seconds 8
$log = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent\runtimes') -Recurse -Filter '20260810.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Write-Host "LOG=$($log.FullName)"
    Get-Content -LiteralPath $log.FullName -Tail 20
}
