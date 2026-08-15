function Show-Range($path, $start, $end) {
    $lines = Get-Content -LiteralPath $path
    for ($n=$start; $n -le $end -and $n -le $lines.Count; $n++) {
        Write-Host ("{0}:{1}" -f $n, $lines[$n-1])
    }
}
Write-Host '=== controller 30-60 ==='
Show-Range 'Controllers\AbutmentRebarController.cs' 30 60
Write-Host '=== controller 105-160 ==='
Show-Range 'Controllers\AbutmentRebarController.cs' 105 160
Write-Host '=== controller 495-520 ==='
Show-Range 'Controllers\AbutmentRebarController.cs' 495 520
Write-Host '=== vm 45-80 ==='
Show-Range 'ViewModels\AbutmentRebarViewModel.cs' 45 80
Write-Host '=== vm 280-300 ==='
Show-Range 'ViewModels\AbutmentRebarViewModel.cs' 280 300
