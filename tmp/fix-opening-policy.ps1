$ErrorActionPreference = 'Stop'
$path = Resolve-Path 'Resources\Presets\cau-van-cui-m2.v1.json'
$raw = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
$j = $raw | ConvertFrom-Json
$ids = @('CVC-W1','CVC-W2','CVC-W3','CVC-A1','CVC-A2','CVC-A3')
foreach ($r in $j.rules) {
    if ($ids -contains $r.ruleId) {
        $r.openingObstaclePolicy = 'ApprovedDetail'
        if ([string]::IsNullOrWhiteSpace([string]$r.obstacleEvidenceId)) {
            $r.obstacleEvidenceId = 'CVC-P39'
        }
        Write-Host ("{0} opening={1} pile={2} ev={3}" -f $r.ruleId, $r.openingObstaclePolicy, $r.pileObstaclePolicy, $r.obstacleEvidenceId)
    }
}
$j.ruleVersion = '2026-08-10.3'
$j.ruleHash = ''
$json = $j | ConvertTo-Json -Depth 100
# ConvertTo-Json may reorder; acceptable for runtime
[IO.File]::WriteAllText($path, $json + "`r`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host ("version=" + $j.ruleVersion)
