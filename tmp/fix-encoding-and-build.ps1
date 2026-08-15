$ErrorActionPreference = 'Stop'
$path = Join-Path (Get-Location) 'Controllers\AbutmentRebarController.cs'
$utf8 = New-Object System.Text.UTF8Encoding $false
$text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)

# ensure field not readonly
$text = $text -replace 'private readonly RebarTypeCatalog _barTypeCatalog;', 'private RebarTypeCatalog _barTypeCatalog;'

# fix known user-facing strings (replace whole property bodies / messages)
$text = [regex]::Replace($text,
    'public string RebarBarTypeCatalogSummary =>[\s\S]*?string\.Empty\);',
    @'
public string RebarBarTypeCatalogSummary =>
        _barTypeCatalog.IsEmpty
            ? "Model chua co RebarBarType."
            : string.Join(", ", AvailableBarTypeNames.Take(16)) +
              (AvailableBarTypeNames.Count > 16 ? ", ..." : string.Empty);
'@)

$text = $text -replace 'throw new InvalidOperationException\(\$"Rebar type ''\{name\}''[^"]*"\);',
    'throw new InvalidOperationException($"Rebar type ''{name}'' khong ton tai trong model.");'

$text = $text -replace '"Model[^"]*RebarBarType\. H[^"]*D32[^"]*"',
    '"Model chua co RebarBarType. Hay tao/load type D12/D16/D20/D25/D32 truoc khi rai thep mo."'

$text = $text -replace '"L[^"]*ke hoach thep that bai: "',
    '"Lap ke hoach thep that bai: "'
# broader replace for plan build failed message line
$text = [regex]::Replace($text,
    '"L[^"]{0,40}ke ho[^"]{0,20}th[^"]{0,10}th[^"]{0,10}b[^"]{0,10}: "',
    '"Lap ke hoach thep that bai: "')

[IO.File]::WriteAllText($path, $text, $utf8)

# ViewModel forced notices
$vm = Join-Path (Get-Location) 'ViewModels\AbutmentRebarViewModel.cs'
$vtext = [IO.File]::ReadAllText($vm, [Text.Encoding]::UTF8)
$vtext = $vtext -replace '"Model[^"]*RebarBarType\. T[^"]*D32[^"]*"',
    '"Model chua co RebarBarType. Tao/load D12/D16/D20/D25/D32 roi Phan tich lai."'
$vtext = $vtext -replace '"Thi[^"]*RebarBarType trong model"',
    '"Thieu RebarBarType trong model"'
$vtext = $vtext -replace '"Model chua co RebarBarType\. Hay tao/load type D12/D16/D20/D25/D32 roi Phan tich lai\."',
    '"Model chua co RebarBarType. Hay tao/load type D12/D16/D20/D25/D32 roi Phan tich lai."'
# gate message
$vtext = $vtext -replace 'return "Model[^"]*RebarBarType[^"]*";',
    'return "Model chua co RebarBarType. Hay tao/load type D12/D16/D20/D25/D32 roi Phan tich lai.";'
[IO.File]::WriteAllText($vm, $vtext, $utf8)

Write-Host 'Encoding-safe strings applied'
