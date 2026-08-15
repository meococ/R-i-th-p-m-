$ErrorActionPreference = 'Stop'
$path = Join-Path (Get-Location) 'ViewModels\AbutmentRebarViewModel.cs'
$lines = Get-Content -LiteralPath $path -Encoding UTF8
$out = New-Object System.Collections.Generic.List[string]
for ($i=0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match 'AvailableBarTypeNames = controller\.AvailableBarTypeNames;') {
        # drop assignment; property will read live
        continue
    }
    if ($line -match 'public IReadOnlyList<string> AvailableBarTypeNames \{ get; \}') {
        $out.Add('    public IReadOnlyList<string> AvailableBarTypeNames => _controller.AvailableBarTypeNames;')
        continue
    }
    # After UpdateIssues(plan.Issues) in analyze success path, notify bar type list
    if ($line -match 'UpdateQuantitiesFromPlan\(plan\);') {
        $out.Add($line)
        # peek if next blocks already have notify
        $out.Add('            OnPropertyChanged(nameof(AvailableBarTypeNames));')
        continue
    }
    $out.Add($line)
}
[System.IO.File]::WriteAllText($path, ($out -join "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))
Write-Host 'PATCHED viewmodel'
Select-String -Path $path -Pattern 'AvailableBarTypeNames' | ForEach-Object { "{0}:{1}" -f $_.LineNumber, $_.Line.Trim() }
