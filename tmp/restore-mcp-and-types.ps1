$ErrorActionPreference = 'Continue'

Write-Host '=== processes ==='
Get-Process -Name Revit, 'BIM765T.Revit.McpHost' -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, MainWindowTitle, Responding |
    Format-Table -AutoSize

Write-Host '=== listening ports (possible MCP) ==='
try {
    Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -in 80,443,5000,5001,5173,7000,7001,8080,8765,9000,9100,18800,18801,28080 } |
        Select-Object LocalAddress, LocalPort, OwningProcess |
        Format-Table -AutoSize
} catch {
    netstat -ano | Select-String 'LISTENING' | Select-String ':80 |:5000|:7000|:8080|:8765|:9000|:18800'
}

Write-Host '=== agent shadow ==='
$shadow = Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent\shadow\2025'
if (Test-Path $shadow) {
    Get-ChildItem $shadow -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name, LastWriteTime
    $current = Join-Path $shadow 'Release-1.0.0.0-r25-current'
    if (Test-Path $current) {
        Get-ChildItem $current -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
    }
}

Write-Host '=== agent logs ==='
$logRoots = @(
    (Join-Path $env:LOCALAPPDATA 'BIM765T.Revit.Agent'),
    (Join-Path $env:LOCALAPPDATA 'BIM765T'),
    (Join-Path $env:APPDATA 'BIM765T')
)
foreach ($root in $logRoots) {
    if (-not (Test-Path $root)) { continue }
    Write-Host "ROOT=$root"
    Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '\.(log|txt|json)$' -and $_.LastWriteTime -gt (Get-Date).AddDays(-2) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 12 FullName, Length, LastWriteTime |
        Format-Table -AutoSize
}

# Source rebar project
$src = 'C:\Users\ADMIN\Downloads\01. Cong Viec DV\Tài Liệu\01_DU_AN\01_DUONG_VEN_BIEN_DAK_LAK\02_Cau_Van_Cui\070626 CAU VAN CUI_ver 25_v2.rvt'
Write-Host ("SRC_EXISTS={0} PATH={1}" -f (Test-Path -LiteralPath $src), $src)
if (Test-Path -LiteralPath $src) {
    Get-Item -LiteralPath $src | Select-Object FullName, Length, LastWriteTime | Format-List
}
