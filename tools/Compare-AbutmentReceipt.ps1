<#
.SYNOPSIS
    Doi chieu bien lai Create cua add-in rai thep mo cau voi bo so ky vong.

.DESCRIPTION
    Doc bien lai JSON ma AbutmentRebarFactory ghi ra
    (%LocalAppData%\DVB_ADDIN\Receipts\Abutment) va, neu co, file CSV bang cat phoi
    ma AbutmentScheduleService xuat ra (%LocalAppData%\DVB_ADDIN\Exports\Abutment),
    roi in bang doi chieu giua so do that va so ky vong.

    Bo so ky vong duoc doc TU CHINH PRESET (Resources\Presets\cau-van-cui-m2.v1.json)
    chu khong go cung trong script: so vi tri thanh, duong kinh, chieu dai noi chong,
    khoi luong rieng, chieu dai phoi kho. Chi bon con so dang la GIA THIET moi nam
    trong script, va chung duoc danh dau ro rang trong bang.

    LUU Y QUAN TRONG: bien lai JSON KHONG chua chieu dai thanh do that. No chi ghi
    dinh danh lan chay, danh sach ElementId, khoa cua tung doan thep va danh sach van
    de. Chieu dai that chi lay duoc tu bang cat phoi (hoac tu tham so DVB_CutLength
    tren tung thanh trong Revit). Vi vay script tu tim ca hai file.

.PARAMETER ReceiptPath
    Duong dan bien lai JSON. Bo trong thi tu lay file moi nhat trong
    %LocalAppData%\DVB_ADDIN\Receipts\Abutment.

.PARAMETER CuttingListPath
    Duong dan file CSV bang cat phoi. Bo trong thi tu lay file moi nhat trong
    %LocalAppData%\DVB_ADDIN\Exports\Abutment.

.PARAMETER PresetPath
    Duong dan preset. Mac dinh la Resources\Presets\cau-van-cui-m2.v1.json cua repo.

.PARAMETER SkipCuttingList
    Bo qua bang cat phoi, chi doi chieu phan bien lai kiem duoc.

.PARAMETER LengthToleranceMm
    Sai so chieu dai coi la khop. Mac dinh 1 mm.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Compare-AbutmentReceipt.ps1"

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Compare-AbutmentReceipt.ps1" `
        -ReceiptPath "D:\bang-chung\20260813T092000Z-abc.json"

.NOTES
    Ma thoat: 0 = moi thu khop; 1 = co lech hoac co loi chan; 2 = khong doc duoc dau vao.
#>
[CmdletBinding()]
param(
    [string]$ReceiptPath,
    [string]$CuttingListPath,
    [string]$PresetPath,
    [switch]$SkipCuttingList,
    [double]$LengthToleranceMm = 1.0
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Bon con so GIA THIET. Day la toan bo phan go cung cua script, va cung la thu
# ma lan chay that phai thay. Nguon: AGENT.md muc 40.8 va 41.8.
# ---------------------------------------------------------------------------
$Assumption = @{
    'CVC-F1'   = @{ BarLengthMm = 17250.0; CutShortMm = 7200.0; CutLongMm = 10700.0 }
    'CVC-F2'   = @{ BarLengthMm = 17250.0; CutShortMm = 7700.0; CutLongMm = 11100.0 }
    'CVC-F3-T' = @{ BarLengthMm = 6250.0;  CutShortMm = $null;  CutLongMm = $null }
    'CVC-F3-B' = @{ BarLengthMm = 6250.0;  CutShortMm = $null;  CutLongMm = $null }
}

# ArrayList chu khong phai List[T]: Windows PowerShell 5.1 nem "Argument types do not
# match" khi boc mot List[T] lay tu hashtable vao @( ).
$script:Findings = New-Object System.Collections.ArrayList
$script:HasBlocker = $false

# ---------------------------------------------------------------------------
# Ham dung chung
# ---------------------------------------------------------------------------
function Write-Line {
    param([string]$Text = '')
    Write-Output $Text
}

function Write-Section {
    param([string]$Title)
    Write-Line
    Write-Line ('=' * 90)
    Write-Line $Title
    Write-Line ('=' * 90)
}

function Write-Row {
    param([string[]]$Cells, [int[]]$Widths)
    $parts = @()
    for ($i = 0; $i -lt $Cells.Count; $i++) {
        $value = [string]$Cells[$i]
        if ($value.Length -gt $Widths[$i]) { $value = $value.Substring(0, $Widths[$i]) }
        $parts += $value.PadRight($Widths[$i])
    }
    Write-Line (($parts -join '  ').TrimEnd())
}

function Write-Divider {
    param([int[]]$Widths)
    $cells = @()
    foreach ($width in $Widths) { $cells += ('-' * $width) }
    Write-Row -Cells $cells -Widths $Widths
}

function Add-Finding {
    param([string]$Text, [switch]$Blocker)
    $null = $script:Findings.Add($Text)
    if ($Blocker) { $script:HasBlocker = $true }
}

function Get-Prop {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $member = $Object.PSObject.Properties[$Name]
    if ($null -eq $member) { return $Default }
    if ($null -eq $member.Value) { return $Default }
    return $member.Value
}

function Get-Verdict {
    param($Expected, $Actual, [double]$Tolerance = 0.0)
    if ($null -eq $Actual) { return 'KHONG DO' }
    if ($null -eq $Expected) { return 'CHUA CO KV' }
    if ([Math]::Abs([double]$Expected - [double]$Actual) -le $Tolerance) { return 'KHOP' }
    return 'LECH'
}

function Format-Number {
    # Khong dung nhom hang nghin: dau phay o day de bi doc nham la dau thap phan.
    param($Value, [int]$Decimals = 0)
    if ($null -eq $Value) { return '-' }
    $format = '0'
    if ($Decimals -gt 0) { $format = '0.' + ('0' * $Decimals) }
    return ([double]$Value).ToString($format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Resolve-NewestFile {
    param([string]$Folder, [string]$Filter)
    if ([string]::IsNullOrWhiteSpace($Folder)) { return $null }
    if (-not (Test-Path -LiteralPath $Folder)) { return $null }
    $newest = Get-ChildItem -LiteralPath $Folder -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $newest) { return $null }
    return $newest.FullName
}

function ConvertTo-Double {
    param([string]$Text, [string]$DecimalSeparator)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $clean = $Text.Trim().Replace(' ', '')
    if ($DecimalSeparator -eq ',') {
        $clean = $clean.Replace('.', '').Replace(',', '.')
    }
    else {
        $clean = $clean.Replace(',', '')
    }
    $parsed = 0.0
    $styles = [System.Globalization.NumberStyles]::Float
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    if ([double]::TryParse($clean, $styles, $invariant, [ref]$parsed)) { return $parsed }
    return $null
}

# ---------------------------------------------------------------------------
# 1. Tim ba file dau vao
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($PresetPath)) {
    $PresetPath = Join-Path $PSScriptRoot '..\Resources\Presets\cau-van-cui-m2.v1.json'
}
if (-not (Test-Path -LiteralPath $PresetPath)) {
    Write-Line "LOI: khong tim thay preset: $PresetPath"
    exit 2
}
$PresetPath = (Resolve-Path -LiteralPath $PresetPath).Path

$receiptRoot = Join-Path $env:LOCALAPPDATA 'DVB_ADDIN\Receipts\Abutment'
$exportRoot = Join-Path $env:LOCALAPPDATA 'DVB_ADDIN\Exports\Abutment'

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Resolve-NewestFile -Folder $receiptRoot -Filter '*.json'
    if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
        Write-Line "LOI: khong co bien lai nao trong $receiptRoot"
        Write-Line '     Chay Tao thep moi (hoac Tao lai khu vuc) truoc, hoac truyen -ReceiptPath.'
        exit 2
    }
}
if (-not (Test-Path -LiteralPath $ReceiptPath)) {
    Write-Line "LOI: khong tim thay bien lai: $ReceiptPath"
    exit 2
}
$ReceiptPath = (Resolve-Path -LiteralPath $ReceiptPath).Path

if (-not $SkipCuttingList -and [string]::IsNullOrWhiteSpace($CuttingListPath)) {
    $CuttingListPath = Resolve-NewestFile -Folder $exportRoot -Filter '*.csv'
}
if ($SkipCuttingList) { $CuttingListPath = '' }
if (-not [string]::IsNullOrWhiteSpace($CuttingListPath)) {
    if (-not (Test-Path -LiteralPath $CuttingListPath)) {
        Write-Line "LOI: khong tim thay bang cat phoi: $CuttingListPath"
        exit 2
    }
    $CuttingListPath = (Resolve-Path -LiteralPath $CuttingListPath).Path
}

# ---------------------------------------------------------------------------
# 2. Doc preset -> bo so ky vong
# ---------------------------------------------------------------------------
try {
    $preset = Get-Content -Raw -LiteralPath $PresetPath -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Write-Line "LOI: khong doc duoc preset JSON: $($_.Exception.Message)"
    exit 2
}

$standard = Get-Prop $preset 'standard'
$steelDensity = [double](Get-Prop $standard 'steelDensityKgPerM3' 0)
$stockBarLengthMm = [double](Get-Prop $standard 'stockBarLengthMm' 0)
$presetRuleHash = [string](Get-Prop $preset 'ruleHash' '')
$presetRuleVersion = [string](Get-Prop $preset 'ruleVersion' '')

$expected = @()
foreach ($rule in (Get-Prop $preset 'rules' @())) {
    if (-not (Get-Prop $rule 'enabled' $false)) { continue }
    if ((Get-Prop $rule 'zone' '') -ne 'Footing') { continue }

    $ruleId = [string](Get-Prop $rule 'ruleId' '')
    $lapSplice = Get-Prop $rule 'lapSplice'
    $lapLengthMm = 0.0
    $piecesPerPosition = 1
    if ($null -ne $lapSplice -and (Get-Prop $lapSplice 'requirement' '') -eq 'Required') {
        $lapLengthMm = [double](Get-Prop $lapSplice 'lapLengthMm' 0)
        # Mot moi noi moi vi tri thanh -> hai doan, dung ket luan AGENT.md muc 40.2:
        # thanh doc dai hon phoi kho nen bat buoc dung MOT moi noi.
        $piecesPerPosition = 2
    }

    $diameterMm = [double](Get-Prop $rule 'diameterMm' 0)
    $unitMassKgPerM = $steelDensity * [Math]::PI * $diameterMm * $diameterMm / 4.0 * 1e-6
    $stationSchedule = Get-Prop $rule 'stationSchedule'
    $positions = [int](Get-Prop $stationSchedule 'expectedBarCount' 0)

    $assumedBarLengthMm = $null
    $assumedCutShortMm = $null
    $assumedCutLongMm = $null
    if ($Assumption.ContainsKey($ruleId)) {
        $assumedBarLengthMm = $Assumption[$ruleId].BarLengthMm
        $assumedCutShortMm = $Assumption[$ruleId].CutShortMm
        $assumedCutLongMm = $Assumption[$ruleId].CutLongMm
    }

    $spliceCount = $piecesPerPosition - 1
    $assumedTotalLengthMm = $null
    $assumedMassKg = $null
    if ($null -ne $assumedBarLengthMm) {
        $assumedTotalLengthMm = $positions * ($assumedBarLengthMm + $spliceCount * $lapLengthMm)
        $assumedMassKg = $assumedTotalLengthMm / 1000.0 * $unitMassKgPerM
    }

    $expected += [pscustomobject]@{
        RuleId               = $ruleId
        CanonicalMark        = [string](Get-Prop $rule 'canonicalMark' '')
        DrawingMark          = [string](Get-Prop $rule 'sourceDrawingMark' '')
        DiameterMm           = $diameterMm
        Positions            = $positions
        PiecesPerPosition    = $piecesPerPosition
        Objects              = $positions * $piecesPerPosition
        LapLengthMm          = $lapLengthMm
        UnitMassKgPerM       = $unitMassKgPerM
        AssumedBarLengthMm   = $assumedBarLengthMm
        AssumedCutShortMm    = $assumedCutShortMm
        AssumedCutLongMm     = $assumedCutLongMm
        AssumedTotalLengthMm = $assumedTotalLengthMm
        AssumedMassKg        = $assumedMassKg
    }
}

if ($expected.Count -eq 0) {
    Write-Line 'LOI: preset khong co rule Footing nao dang bat. Khong co gi de doi chieu.'
    exit 2
}
$expectedRuleIds = @($expected | ForEach-Object { $_.RuleId })

# ---------------------------------------------------------------------------
# 3. Doc bien lai
# ---------------------------------------------------------------------------
try {
    $receipt = Get-Content -Raw -LiteralPath $ReceiptPath -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Write-Line "LOI: khong doc duoc bien lai JSON: $($_.Exception.Message)"
    exit 2
}

$createdItems = @(Get-Prop $receipt 'CreatedItems' @())
$createdIds = @(Get-Prop $receipt 'CreatedIds' @())
$removedIds = @(Get-Prop $receipt 'RemovedIds' @())
$issues = @(Get-Prop $receipt 'Issues' @())

# Khoa cua mot doan thep, theo AbutmentBarPieceKernel.FormatPieceKey:
#   "<ruleId>-<vi tri 3 chu so>"            khi lop khong noi
#   "<ruleId>-<vi tri 3 chu so>-P<so doan>" khi lop co noi
$piecePattern = '^(?<rule>.+?)-(?<pos>\d{3})-P(?<piece>\d+)$'
$positionPattern = '^(?<rule>.+?)-(?<pos>\d{3})$'

$measuredByRule = @{}
$unparsedKeys = New-Object System.Collections.ArrayList
foreach ($item in $createdItems) {
    $key = [string](Get-Prop $item 'Key' '')
    $ruleId = ''
    $position = 0
    if ($key -match $piecePattern) {
        $ruleId = $Matches['rule']
        $position = [int]$Matches['pos']
    }
    elseif ($key -match $positionPattern) {
        $ruleId = $Matches['rule']
        $position = [int]$Matches['pos']
    }
    else {
        $null = $unparsedKeys.Add($key)
        continue
    }
    if (-not $measuredByRule.ContainsKey($ruleId)) {
        $measuredByRule[$ruleId] = @{
            Objects   = 0
            Positions = New-Object System.Collections.Generic.HashSet[int]
            ZoneCodes = New-Object System.Collections.Generic.HashSet[string]
        }
    }
    $measuredByRule[$ruleId].Objects++
    $null = $measuredByRule[$ruleId].Positions.Add($position)
    $null = $measuredByRule[$ruleId].ZoneCodes.Add([string](Get-Prop $item 'ZoneCode' ''))
}

# ---------------------------------------------------------------------------
# 4. Doc bang cat phoi (neu co)
# ---------------------------------------------------------------------------
$cutByMark = @{}
$cuttingListTotals = $null
$cuttingListNotes = @()
if (-not [string]::IsNullOrWhiteSpace($CuttingListPath)) {
    $rawLines = @(Get-Content -LiteralPath $CuttingListPath -Encoding UTF8 |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $cuttingListNotes = @($rawLines | Where-Object { $_.TrimStart().StartsWith('#') })
    $dataLines = @($rawLines | Where-Object { -not $_.TrimStart().StartsWith('#') })

    if ($dataLines.Count -lt 2) {
        Add-Finding 'Bang cat phoi khong co dong du lieu nao.' -Blocker
    }
    else {
        $header = $dataLines[0]
        $separator = "`t"
        $bestFieldCount = @($header -split "`t").Count
        foreach ($candidate in @(';', ',')) {
            $count = @($header -split [regex]::Escape($candidate)).Count
            if ($count -gt $bestFieldCount) {
                $bestFieldCount = $count
                $separator = $candidate
            }
        }
        $decimalSeparator = '.'
        if ($separator -eq ';') { $decimalSeparator = ',' }

        for ($i = 1; $i -lt $dataLines.Count; $i++) {
            $fields = @($dataLines[$i] -split [regex]::Escape($separator))
            if ($fields.Count -lt 8) { continue }
            $mark = $fields[0].Trim()
            if ($mark -eq 'TONG') {
                $cuttingListTotals = [pscustomobject]@{
                    PieceCount    = ConvertTo-Double $fields[4] $decimalSeparator
                    TotalLengthMm = ConvertTo-Double $fields[5] $decimalSeparator
                    MassKg        = ConvertTo-Double $fields[7] $decimalSeparator
                }
                continue
            }
            if ([string]::IsNullOrWhiteSpace($mark)) { continue }
            if (-not $cutByMark.ContainsKey($mark)) {
                $cutByMark[$mark] = New-Object System.Collections.ArrayList
            }
            $null = $cutByMark[$mark].Add([pscustomobject]@{
                    DrawingMark    = $fields[1].Trim()
                    DiameterMm     = ConvertTo-Double $fields[2] $decimalSeparator
                    CutLengthMm    = ConvertTo-Double $fields[3] $decimalSeparator
                    PieceCount     = ConvertTo-Double $fields[4] $decimalSeparator
                    TotalLengthMm  = ConvertTo-Double $fields[5] $decimalSeparator
                    UnitMassKgPerM = ConvertTo-Double $fields[6] $decimalSeparator
                    MassKg         = ConvertTo-Double $fields[7] $decimalSeparator
                })
        }
    }
}

# ---------------------------------------------------------------------------
# 5. In bao cao
# ---------------------------------------------------------------------------
Write-Line
Write-Line 'DOI CHIEU BIEN LAI RAI THEP MO CAU'
Write-Line ('Chay luc ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Write-Line
Write-Line ('Bien lai      : ' + $ReceiptPath)
if ([string]::IsNullOrWhiteSpace($CuttingListPath)) {
    Write-Line 'Bang cat phoi : (khong co) -> chieu dai va khoi luong se KHONG do duoc'
}
else {
    Write-Line ('Bang cat phoi : ' + $CuttingListPath)
}
Write-Line ('Preset        : ' + $PresetPath)

# --- A -----------------------------------------------------------------
Write-Section 'A. THONG TIN LAN CHAY'

$headWidths = @(20, 66)
$succeeded = [bool](Get-Prop $receipt 'Succeeded' $false)
$persisted = [bool](Get-Prop $receipt 'ReceiptPersisted' $false)
$receiptRuleHash = [string](Get-Prop $receipt 'RuleHash' '')
$hashMatches = (-not [string]::IsNullOrWhiteSpace($receiptRuleHash)) -and
    ($receiptRuleHash.Trim().ToUpperInvariant() -eq $presetRuleHash.Trim().ToUpperInvariant())

$successText = 'false  -> HONG, lan chay nay khong tao duoc thep'
if ($succeeded) { $successText = 'true   -> DAT' }
$persistText = 'false  -> HONG'
if ($persisted) { $persistText = 'true   -> DAT' }
$hashText = $receiptRuleHash + '  -> LECH PRESET'
if ($hashMatches) { $hashText = $receiptRuleHash + '  -> DAT' }

Write-Row -Cells @('Truong', 'Gia tri') -Widths $headWidths
Write-Divider -Widths $headWidths
Write-Row -Cells @('Succeeded', $successText) -Widths $headWidths
Write-Row -Cells @('Operation', [string](Get-Prop $receipt 'Operation' '')) -Widths $headWidths
Write-Row -Cells @('GenerationId', [string](Get-Prop $receipt 'GenerationId' '')) -Widths $headWidths
Write-Row -Cells @('RuleHash bien lai', $hashText) -Widths $headWidths
Write-Row -Cells @('RuleHash preset', ($presetRuleHash + '  (ruleVersion ' + $presetRuleVersion + ')')) -Widths $headWidths
Write-Row -Cells @('ReceiptPersisted', $persistText) -Widths $headWidths
Write-Row -Cells @('DocumentPath', [string](Get-Prop $receipt 'DocumentPath' '')) -Widths $headWidths
Write-Row -Cells @('So Rebar da tao', [string]$createdIds.Count) -Widths $headWidths
Write-Row -Cells @('So Rebar da xoa', [string]$removedIds.Count) -Widths $headWidths

if (-not $succeeded) {
    Add-Finding 'Bien lai bao Succeeded=false: lan chay nay KHONG tao duoc thep.' -Blocker
}
if (-not $hashMatches) {
    Add-Finding ('RuleHash cua bien lai khac preset trong repo. Ban dang chay KHONG phai preset nay, ' +
        'moi con so ben duoi deu vo nghia. Deploy lai roi chay lai.') -Blocker
}
if (-not $persisted) {
    Add-Finding 'Bien lai bao ReceiptPersisted=false.' -Blocker
}
if ($createdIds.Count -ne $createdItems.Count) {
    Add-Finding ('CreatedIds co ' + $createdIds.Count + ' phan tu nhung CreatedItems co ' +
        $createdItems.Count + ' phan tu; hai danh sach phai bang nhau.') -Blocker
}
if ($unparsedKeys.Count -gt 0) {
    $sample = (@($unparsedKeys | Select-Object -First 3) -join ', ')
    Add-Finding ('Khong doc duoc ' + $unparsedKeys.Count + ' khoa doan thep, vi du: ' + $sample) -Blocker
}

# --- B -----------------------------------------------------------------
Write-Section 'B. SO LUONG THEO LOP (kiem duoc tu bien lai)'

$countWidths = @(10, 5, 4, 9, 9, 6, 9, 9, 6, 12)
Write-Row -Cells @('Rule', 'Mark', 'O', 'ViTri KV', 'ViTri DO', 'KQ', 'DoiTg KV', 'DoiTg DO', 'KQ', 'Zone code') -Widths $countWidths
Write-Divider -Widths $countWidths

$totalExpectedPositions = 0
$totalExpectedObjects = 0
$totalMeasuredPositions = 0
$totalMeasuredObjects = 0

foreach ($rule in $expected) {
    $measuredPositions = $null
    $measuredObjects = $null
    $zoneCode = '-'
    if ($measuredByRule.ContainsKey($rule.RuleId)) {
        $measuredPositions = $measuredByRule[$rule.RuleId].Positions.Count
        $measuredObjects = $measuredByRule[$rule.RuleId].Objects
        $zoneCode = (@($measuredByRule[$rule.RuleId].ZoneCodes) | Sort-Object) -join ','
        $totalMeasuredPositions += $measuredPositions
        $totalMeasuredObjects += $measuredObjects
    }
    $totalExpectedPositions += $rule.Positions
    $totalExpectedObjects += $rule.Objects

    $positionVerdict = Get-Verdict $rule.Positions $measuredPositions
    $objectVerdict = Get-Verdict $rule.Objects $measuredObjects
    $measuredPositionText = '-'
    if ($null -ne $measuredPositions) { $measuredPositionText = [string]$measuredPositions }
    $measuredObjectText = '-'
    if ($null -ne $measuredObjects) { $measuredObjectText = [string]$measuredObjects }

    Write-Row -Cells @(
        $rule.RuleId,
        $rule.DrawingMark,
        ('D' + [string][int]$rule.DiameterMm),
        [string]$rule.Positions,
        $measuredPositionText,
        $positionVerdict,
        [string]$rule.Objects,
        $measuredObjectText,
        $objectVerdict,
        $zoneCode) -Widths $countWidths

    if ($positionVerdict -ne 'KHOP') {
        Add-Finding ($rule.RuleId + ': so vi tri thanh ky vong ' + $rule.Positions +
            ', bien lai co ' + $measuredPositionText + '.') -Blocker
    }
    if ($objectVerdict -ne 'KHOP') {
        Add-Finding ($rule.RuleId + ': so doi tuong Rebar ky vong ' + $rule.Objects +
            ', bien lai co ' + $measuredObjectText + '.') -Blocker
    }
}

Write-Divider -Widths $countWidths
Write-Row -Cells @(
    'TONG', '', '',
    [string]$totalExpectedPositions,
    [string]$totalMeasuredPositions,
    (Get-Verdict $totalExpectedPositions $totalMeasuredPositions),
    [string]$totalExpectedObjects,
    [string]$totalMeasuredObjects,
    (Get-Verdict $totalExpectedObjects $totalMeasuredObjects),
    '') -Widths $countWidths

foreach ($extra in @($measuredByRule.Keys)) {
    if ($expectedRuleIds -contains $extra) { continue }
    Add-Finding ("Bien lai co rule '" + $extra + "' ma preset khong bat. " +
        'Dang chay preset khac hoac model con thep cu.') -Blocker
}

# --- C -----------------------------------------------------------------
Write-Section 'C. CHIEU DAI VA KHOI LUONG (chi lay duoc tu bang cat phoi)'

if ([string]::IsNullOrWhiteSpace($CuttingListPath)) {
    Write-Line 'Khong co bang cat phoi nen KHONG do duoc chieu dai thanh that.'
    Write-Line
    Write-Line 'Bien lai JSON KHONG chua chieu dai thanh, khong chua cap chieu dai cat, khong'
    Write-Line 'chua khoi luong. No chi ghi dinh danh lan chay, danh sach ElementId, khoa cua'
    Write-Line 'tung doan thep va danh sach van de.'
    Write-Line
    Write-Line 'Muon co so do that hay chay:'
    Write-Line '  ribbon DVB_ADDIN -> Bang Cat Phoi Thep Mo -> chon mo'
    Write-Line 'roi chay lai script nay. File CSV se nam o:'
    Write-Line ('  ' + $exportRoot)
    Add-Finding 'Chua co bang cat phoi nen bon con so gia thiet van chua duoc thay bang so do.'
}
else {
    foreach ($note in $cuttingListNotes) { Write-Line $note }
    Write-Line

    $lenWidths = @(10, 6, 18, 14, 14, 13)
    Write-Row -Cells @('Rule', 'Mark', 'Dai luong', 'Ky vong', 'Do that', 'KQ') -Widths $lenWidths
    Write-Divider -Widths $lenWidths

    $measuredTotalMassKg = 0.0
    $expectedTotalMassKg = 0.0
    $anyMassMeasured = $false

    foreach ($rule in $expected) {
        $mark = $rule.CanonicalMark
        $rows = @()
        if ($cutByMark.ContainsKey($mark)) { $rows = @($cutByMark[$mark]) }

        if ($null -ne $rule.AssumedMassKg) { $expectedTotalMassKg += $rule.AssumedMassKg }

        if ($rows.Count -eq 0) {
            Write-Row -Cells @($rule.RuleId, $mark, 'chieu dai cat', '-', '-', 'KHONG DO') -Widths $lenWidths
            Add-Finding ($rule.RuleId + ": bang cat phoi khong co so hieu '" + $mark + "'.") -Blocker
            Write-Line
            continue
        }

        $sortedCuts = @($rows | Sort-Object CutLengthMm)
        $measuredShort = $sortedCuts[0].CutLengthMm
        $measuredLong = $null
        if ($sortedCuts.Count -ge 2) { $measuredLong = $sortedCuts[$sortedCuts.Count - 1].CutLengthMm }
        if ($sortedCuts.Count -gt 2) {
            Add-Finding ($rule.RuleId + ': bang cat phoi co ' + $sortedCuts.Count +
                ' chieu dai cat khac nhau; mot lop chi duoc co MOT cap cat.') -Blocker
        }

        $measuredBarLengthMm = $measuredShort
        if ($rule.PiecesPerPosition -ge 2) {
            if ($null -eq $measuredLong) {
                $measuredBarLengthMm = $null
                Add-Finding ($rule.RuleId + ': lop nay phai co hai chieu dai cat nhung bang cat phoi ' +
                    'chi co mot. Moi noi khong duoc dung.') -Blocker
            }
            else {
                $measuredBarLengthMm = $measuredShort + $measuredLong - $rule.LapLengthMm
            }

            Write-Row -Cells @($rule.RuleId, $mark, 'cat ngan (mm)',
                (Format-Number $rule.AssumedCutShortMm 0),
                (Format-Number $measuredShort 0),
                (Get-Verdict $rule.AssumedCutShortMm $measuredShort $LengthToleranceMm)) -Widths $lenWidths
            Write-Row -Cells @('', '', 'cat dai (mm)',
                (Format-Number $rule.AssumedCutLongMm 0),
                (Format-Number $measuredLong 0),
                (Get-Verdict $rule.AssumedCutLongMm $measuredLong $LengthToleranceMm)) -Widths $lenWidths
            Write-Row -Cells @('', '', 'noi chong (mm)',
                (Format-Number $rule.LapLengthMm 0),
                (Format-Number $rule.LapLengthMm 0),
                'THEO PRESET') -Widths $lenWidths
        }
        else {
            Write-Row -Cells @($rule.RuleId, $mark, 'chieu dai cat (mm)',
                (Format-Number $rule.AssumedBarLengthMm 0),
                (Format-Number $measuredShort 0),
                (Get-Verdict $rule.AssumedBarLengthMm $measuredShort $LengthToleranceMm)) -Widths $lenWidths
        }

        $barVerdict = Get-Verdict $rule.AssumedBarLengthMm $measuredBarLengthMm $LengthToleranceMm
        Write-Row -Cells @('', '', 'THANH HOAN THIEN',
            (Format-Number $rule.AssumedBarLengthMm 0),
            (Format-Number $measuredBarLengthMm 0),
            $barVerdict) -Widths $lenWidths

        if ($barVerdict -eq 'LECH') {
            $delta = [double]$measuredBarLengthMm - [double]$rule.AssumedBarLengthMm
            Add-Finding ($rule.RuleId + ': chieu dai thanh do that ' +
                (Format-Number $measuredBarLengthMm 1) + ' mm, gia thiet ' +
                (Format-Number $rule.AssumedBarLengthMm 0) + ' mm, lech ' +
                (Format-Number $delta 1) + ' mm. CAP NHAT lai bo so gia thiet.') -Blocker
        }

        $measuredPieces = (@($rows) | Measure-Object -Property PieceCount -Sum).Sum
        $pieceVerdict = Get-Verdict $rule.Objects $measuredPieces
        Write-Row -Cells @('', '', 'so doan',
            [string]$rule.Objects,
            (Format-Number $measuredPieces 0),
            $pieceVerdict) -Widths $lenWidths
        if ($pieceVerdict -ne 'KHOP') {
            Add-Finding ($rule.RuleId + ': bang cat phoi co ' + (Format-Number $measuredPieces 0) +
                ' doan, ky vong ' + $rule.Objects + '.') -Blocker
        }

        $measuredMassKg = (@($rows) | Measure-Object -Property MassKg -Sum).Sum
        if ($null -ne $measuredMassKg) {
            $anyMassMeasured = $true
            $measuredTotalMassKg += [double]$measuredMassKg
        }
        Write-Row -Cells @('', '', 'khoi luong (kg)',
            (Format-Number $rule.AssumedMassKg 0),
            (Format-Number $measuredMassKg 0),
            (Get-Verdict $rule.AssumedMassKg $measuredMassKg 1.0)) -Widths $lenWidths
        Write-Line
    }

    Write-Divider -Widths $lenWidths
    $massVerdict = 'KHONG DO'
    if ($anyMassMeasured) { $massVerdict = Get-Verdict $expectedTotalMassKg $measuredTotalMassKg 2.0 }
    Write-Row -Cells @('TONG', '', 'khoi luong (kg)',
        (Format-Number $expectedTotalMassKg 0),
        (Format-Number $measuredTotalMassKg 0),
        $massVerdict) -Widths $lenWidths

    if ($null -ne $cuttingListTotals) {
        $csvPieceVerdict = Get-Verdict $totalExpectedObjects $cuttingListTotals.PieceCount
        Write-Row -Cells @('TONG', '', 'so doan (TONG)',
            [string]$totalExpectedObjects,
            (Format-Number $cuttingListTotals.PieceCount 0),
            $csvPieceVerdict) -Widths $lenWidths
        if ($csvPieceVerdict -ne 'KHOP') {
            Add-Finding ('Dong TONG cua bang cat phoi khong khop tong so doi tuong ky vong (' +
                $totalExpectedObjects + ').') -Blocker
        }
    }

    Write-Line
    Write-Line 'Doi chieu cheo hai bang: tong chieu dai doan cat phai bang'
    Write-Line '  so thanh x (chieu dai thanh + so noi x chieu dai noi)'
    Write-Line 'Add-in tu kiem dieu nay bang cong ABUTMENT_SCHEDULE_LENGTH_NOT_RECONCILED.'
}

# --- D -----------------------------------------------------------------
Write-Section 'D. VAN DE GHI TRONG BIEN LAI'

if ($issues.Count -eq 0) {
    Write-Line 'Khong co van de nao.'
}
else {
    $errorCount = 0
    foreach ($issue in $issues) {
        $severity = [string](Get-Prop $issue 'Severity' '')
        $code = [string](Get-Prop $issue 'Code' '')
        $message = [string](Get-Prop $issue 'Message' '')
        Write-Line ('[' + $severity.ToUpperInvariant() + '] ' + $code)
        Write-Line ('    ' + $message)
        if ($severity -eq 'Error') {
            $errorCount++
            Add-Finding ('Bien lai co loi chan: ' + $code) -Blocker
        }
    }
    Write-Line
    Write-Line ('Tong ' + $issues.Count + ' van de, trong do ' + $errorCount + ' la loi chan.')
}

# --- E -----------------------------------------------------------------
Write-Section 'E. KET LUAN'

Write-Line 'Nhung con so duoi day la GIA THIET, khong phai so do:'
foreach ($rule in $expected) {
    if ($null -eq $rule.AssumedBarLengthMm) { continue }
    if ($rule.PiecesPerPosition -ge 2) {
        Write-Line ('  ' + $rule.RuleId + ': thanh ' + (Format-Number $rule.AssumedBarLengthMm 0) +
            ' mm, cap cat ' + (Format-Number $rule.AssumedCutShortMm 0) + ' + ' +
            (Format-Number $rule.AssumedCutLongMm 0) + ' mm')
    }
    else {
        Write-Line ('  ' + $rule.RuleId + ': thanh ' + (Format-Number $rule.AssumedBarLengthMm 0) + ' mm')
    }
}
Write-Line ('Phoi kho ' + (Format-Number $stockBarLengthMm 0) + ' mm, khoi luong rieng ' +
    (Format-Number $steelDensity 0) + ' kg/m3 (doc tu preset, khong go cung).')
Write-Line

if ($script:Findings.Count -eq 0) {
    Write-Line 'KET QUA: TAT CA DEU KHOP. Khong co diem nao lech.'
    exit 0
}

Write-Line ('KET QUA: co ' + $script:Findings.Count + ' diem can xu ly.')
Write-Line
$index = 0
foreach ($finding in $script:Findings) {
    $index++
    Write-Line ('  ' + $index + '. ' + $finding)
}

if ($script:HasBlocker) { exit 1 }
exit 0
