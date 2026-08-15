Write-Host '=== XAML Zone Combo ==='
$x = Get-Content -LiteralPath 'Views\AbutmentRebarView.xaml'
for ($i=190; $i -le 230 -and $i -le $x.Count; $i++) { '{0}:{1}' -f $i, $x[$i-1] }

Write-Host '=== VM RefreshZoneOptions ==='
$v = Get-Content -LiteralPath 'ViewModels\AbutmentRebarViewModel.cs'
for ($i=0; $i -lt $v.Count; $i++) {
  if ($v[$i] -match 'RefreshZoneOptions|AvailableZones|SelectedZoneKind|ZoneDisplayName|record AbutmentZoneOption') {
    '{0}:{1}' -f ($i+1), $v[$i]
  }
}
Write-Host '=== method body ==='
$start = ($v | Select-String -Pattern 'private void RefreshZoneOptions').LineNumber | Select-Object -First 1
if ($start) {
  for ($i=$start; $i -le [Math]::Min($start+60, $v.Count); $i++) { '{0}:{1}' -f $i, $v[$i-1] }
}
