$ErrorActionPreference = 'Stop'

# Keep only newest MCP host
$hosts = @(Get-Process -Name 'BIM765T.Revit.McpHost' -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending)
if ($hosts.Count -gt 1) {
    $hosts | Select-Object -Skip 1 | ForEach-Object {
        Write-Host ("Kill extra MCP host PID={0}" -f $_.Id)
        Stop-Process -Id $_.Id -Force
    }
}
if (@(Get-Process -Name 'BIM765T.Revit.McpHost' -ErrorAction SilentlyContinue).Count -eq 0) {
    $exe = 'C:\Users\ADMIN\AppData\Local\BIM765Tbuilds\BIM765Tbuild_v5\McpHost\win-x64\BIM765T.Revit.McpHost.exe'
    if (-not (Test-Path $exe)) {
        $exe = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'BIM765Tbuilds') -Recurse -Filter 'BIM765T.Revit.McpHost.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    Write-Host "Start MCP host $exe"
    Start-Process -FilePath $exe -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 3
}

# Close Revit
$revits = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
foreach ($p in $revits) {
    Write-Host ("Close Revit PID={0}" -f $p.Id)
    try { $null = $p.CloseMainWindow() } catch {}
}
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline -and @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) {
    Start-Sleep -Seconds 2
}
$alive = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($alive.Count -gt 0) { $alive | Stop-Process -Force; Start-Sleep -Seconds 3 }

# Resolve paths with wildcard for unicode folder
$base = 'C:\Users\ADMIN\Downloads\01. Cong Viec DV'
$safe = Get-ChildItem -LiteralPath (Join-Path (Get-Location) 'tmp\review-workspace') -Filter 'SAFE_TEMP_REBAR.rvt' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $safe) { throw 'SAFE_TEMP_REBAR missing' }

$src = Get-ChildItem -Path $base -Recurse -Filter '070626 CAU VAN CUI_ver 25_v2.rvt' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
Write-Host "SAFE=$safe"
Write-Host "SRC=$src"

$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
# Open SAFE first (working copy). User/source rebar file can be opened second via MCP if bridge works.
Write-Host 'Launch Revit SAFE...'
$proc = Start-Process -FilePath $revitExe -ArgumentList @('"' + $safe + '"') -PassThru
Write-Host ("PID={0}" -f $proc.Id)

for ($i=1; $i -le 36; $i++) {
    Start-Sleep -Seconds 5
    $rp = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if ($null -eq $rp) { throw 'Revit exited' }
    $title = $rp.MainWindowTitle
    Write-Host ("T={0} TITLE='{1}'" -f $i, $title)
    if ($title -match 'SAFE_TEMP_REBAR|Revit 2025') {
        if ($title -match 'SAFE_TEMP_REBAR') { break }
    }
}

# Wait for agent registration success
$ok = $false
for ($j=1; $j -le 24; $j++) {
    Start-Sleep -Seconds 5
    $log = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent\runtimes') -Recurse -Filter '20260810.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { continue }
    $tail = Get-Content -LiteralPath $log.FullName -Tail 30 -ErrorAction SilentlyContinue
    $joined = ($tail -join "`n")
    if ($joined -match 'registered|Registration succeeded|Runtime registry is available|Connected|heartbeat' -and $joined -notmatch 'registration will retry') {
        Write-Host 'AGENT_REGISTER_HINT_FOUND'
        $ok = $true
        $tail | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }
        break
    }
    if ($j % 3 -eq 0) {
        Write-Host ("wait agent j={0}" -f $j)
        $tail | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    }
}
Write-Host ("AGENT_OK={0}" -f $ok)
Write-Host ("SRC_FOR_MANUAL_OR_MCP={0}" -f $src)
