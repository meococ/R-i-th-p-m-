$p = 'Domain\GeometryKernel.cs'
$lines = Get-Content -LiteralPath $p
for ($i=120; $i -le 280 -and $i -le $lines.Count; $i++) {
    Write-Host ('{0}:{1}' -f $i, $lines[$i-1])
}
