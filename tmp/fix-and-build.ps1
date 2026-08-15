$ErrorActionPreference = 'Stop'
$p = Resolve-Path 'RevitServices\AbutmentRebarFactory.cs'
$t = [IO.File]::ReadAllText($p)
$t = $t.Replace('if (!plan.CanCreateZone(snapshot.Zone))', 'if (!plan.CanCreateZone(targetZone))')
[IO.File]::WriteAllText($p, $t)
Write-Host 'factory fixed'
