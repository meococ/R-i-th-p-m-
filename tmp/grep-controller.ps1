$p = Join-Path (Get-Location) 'Controllers\AbutmentRebarController.cs'
Write-Host "exists=$(Test-Path -LiteralPath $p) len=$((Get-Item -LiteralPath $p).Length)"
$lines = Get-Content -LiteralPath $p
for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'REBAR_BAR_TYPE|AlignPreset|HasAnyRebar|Analyze\(|_barTypeCatalog') {
        Write-Host ("{0}:{1}" -f ($i+1), $lines[$i])
    }
}
Write-Host '--- Analyze block ---'
for ($i=106; $i -le 180 -and $i -lt $lines.Count; $i++) {
    Write-Host ("{0}:{1}" -f ($i+1), $lines[$i])
}
