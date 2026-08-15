$ErrorActionPreference = 'Stop'
$p = Resolve-Path '..\BIM-DatViet.Tests\AbutmentKernelTests.cs'
$t = [IO.File]::ReadAllText($p)
$t = $t.Replace('Assert.Equal("2026-08-10.2", preset.RuleVersion);', 'Assert.Equal("2026-08-10.3", preset.RuleVersion);')
# optional: assert opening ApprovedDetail for unlocked surface rules
if ($t -notmatch 'CVC-W3.*OpeningObstaclePolicy') {
  $needle = 'Assert.Equal(AbutmentObstaclePolicy.ApprovedDetail, footingRules["CVC-F4"].OpeningObstaclePolicy);'
  $add = @'
Assert.Equal(AbutmentObstaclePolicy.ApprovedDetail, footingRules["CVC-F4"].OpeningObstaclePolicy);
        Assert.Equal(AbutmentObstaclePolicy.ApprovedDetail,
            preset.Rules.Single(rule => rule.RuleId == "CVC-W3").OpeningObstaclePolicy);
        Assert.Equal(AbutmentObstaclePolicy.ApprovedDetail,
            preset.Rules.Single(rule => rule.RuleId == "CVC-A2").OpeningObstaclePolicy);
'@
  if ($t.Contains($needle)) {
    $t = $t.Replace($needle, $add)
  }
}
[IO.File]::WriteAllText($p, $t)
Write-Host 'test updated'
