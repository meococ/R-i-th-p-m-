$ErrorActionPreference = 'Stop'
$revitExe = 'C:\Program Files\Autodesk\Revit 2025\Revit.exe'
$safeModel = (Resolve-Path 'tmp/review-workspace/SAFE_TEMP_REBAR.rvt').Path
$env:BIM_DATVIET_DIAGNOSTIC_HOST_ID = '3770128'
$process = Start-Process -FilePath $revitExe -ArgumentList ('"' + $safeModel + '"') -PassThru
Write-Output "STARTED_PID=$($process.Id)"
