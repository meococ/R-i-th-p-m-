$ErrorActionPreference = 'Stop'
$path = 'RevitServices\AbutmentRebarFactory.cs'
$lines = Get-Content -LiteralPath $path
for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'CanCreateZone\(snapshot\.Zone\)') {
        Write-Host ("L{0}:{1}" -f ($i+1), $lines[$i])
        # show context
        for ($j=[Math]::Max(0,$i-25); $j -le [Math]::Min($lines.Count-1,$i+15); $j++) {
            Write-Host ("{0}:{1}" -f ($j+1), $lines[$j])
        }
    }
}
