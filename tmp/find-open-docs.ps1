$ErrorActionPreference = 'Continue'
$journalRoot = Join-Path $env:LOCALAPPDATA 'Autodesk\Revit\Autodesk Revit 2025\Journals'
$latest = Get-ChildItem $journalRoot -Filter 'journal.*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "JOURNAL=$($latest.FullName) TIME=$($latest.LastWriteTime) LEN=$($latest.Length)"

# Recent open / activate document lines
Select-String -Path $latest.FullName -Pattern 'openFromModelPath|File Name|DocSymbol|Jrn.Activate|SAFE_|\.rvt|\.rfa|RebarBarType|BIM765|Agent|pipe|gRPC|localhost' |
    Select-Object -Last 80 |
    ForEach-Object { $_.Line.Trim() }

Write-Host '--- recent rvt mentions ---'
Select-String -Path $latest.FullName -Pattern '\.rvt' |
    Select-Object -Last 40 |
    ForEach-Object { $_.Line.Trim() }
