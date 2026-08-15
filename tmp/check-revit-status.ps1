$ErrorActionPreference = 'Continue'
Start-Sleep -Seconds 35

$p = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($p.Count -eq 0) {
    Write-Host 'REVIT_GONE'
    exit 1
}

$p | Select-Object Id, ProcessName, MainWindowTitle, Responding, StartTime,
    @{N='WS_MB';E={[math]::Round($_.WorkingSet64/1MB,1)}} |
    Format-List

Get-CimInstance Win32_Process -Filter "Name='Revit.exe'" |
    Select-Object ProcessId, CommandLine |
    Format-List

$addinDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
Write-Host "ADDIN_ROOT=$addinDir"
Get-ChildItem $addinDir -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Get-ChildItem (Join-Path $addinDir 'BIM.DatViet') -Recurse -File |
    Select-Object FullName, Length, LastWriteTime |
    Format-Table -AutoSize

# Parse addin xml if present
$addinXml = Join-Path $addinDir 'BIM.DatViet.addin'
if (Test-Path $addinXml) {
    Write-Host "ADDIN_XML_CONTENT:"
    Get-Content $addinXml
}

# Look for related helper processes
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'BIM|mcp|flow|765|Journal' } |
    Select-Object Id, ProcessName, MainWindowTitle |
    Format-Table -AutoSize
