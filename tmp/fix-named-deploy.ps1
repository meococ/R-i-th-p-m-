$ErrorActionPreference = 'Stop'
$p = Resolve-Path 'ViewModels\AbutmentRebarViewModel.cs'
$t = [IO.File]::ReadAllText($p)
$t = $t.Replace('hasEnabledRules: true));', 'true));')
[IO.File]::WriteAllText($p, $t)
Write-Host 'fixed named arg'
