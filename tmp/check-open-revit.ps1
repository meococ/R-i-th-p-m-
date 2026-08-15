$ErrorActionPreference = 'Continue'

$procs = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    Write-Host 'NO_REVIT'
} else {
    $procs | ForEach-Object {
        Write-Host ("PID={0} TITLE='{1}' RESP={2} WS_MB={3}" -f $_.Id, $_.MainWindowTitle, $_.Responding, [math]::Round($_.WorkingSet64/1MB,1))
    }
    Get-CimInstance Win32_Process -Filter "Name='Revit.exe'" | ForEach-Object {
        Write-Host ("CMD PID={0} LINE={1}" -f $_.ProcessId, $_.CommandLine)
    }
}

Write-Host '--- related ---'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'BIM765|Mcp|Revit' } |
    Select-Object Id, ProcessName, MainWindowTitle |
    Format-Table -AutoSize

$agentAddin = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\BIM765T.Revit.Agent.addin'
Write-Host ("AGENT_ADDIN_EXISTS={0}" -f (Test-Path $agentAddin))
if (Test-Path $agentAddin) {
    Get-Content $agentAddin
}
