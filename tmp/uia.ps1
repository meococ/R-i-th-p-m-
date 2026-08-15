<#
.SYNOPSIS
    Helper UI Automation cho Revit: tim / bam / doc control theo ten.

.DESCRIPTION
    Dung de dieu khien add-in ma khong can toa do pixel. Ba lenh:
      Find   - liet ke control khop ten (dang cay phang)
      Invoke - bam control (InvokePattern, fallback SelectionItem/Toggle/LegacyIAccessible)
      Dump   - do cay control duoi mot goc, gioi han do sau

.EXAMPLE
    .\uia.ps1 -Action Find -Name 'Rai Thep'
    .\uia.ps1 -Action Invoke -Name 'Phan tich'
    .\uia.ps1 -Action Dump -Depth 3
#>
param(
    [ValidateSet('Find', 'Invoke', 'Dump', 'Click')]
    [string]$Action = 'Find',

    [string]$Name = '',

    [int]$Depth = 4,

    [string]$WindowTitleLike = 'Autodesk Revit*',

    [switch]$ExactName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-RevitRoot {
    param([string]$TitleLike)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($root)
    while ($null -ne $child) {
        $t = $child.Current.Name
        if ($t -like $TitleLike) { return $child }
        $child = $walker.GetNextSibling($child)
    }
    return $null
}

# So khop bo dau tieng Viet: nguoi goi go 'Phan tich', UI hien 'Phân tích'.
function Test-NameMatch {
    param([string]$Candidate, [string]$Pattern, [bool]$Exact)
    if ([string]::IsNullOrWhiteSpace($Pattern)) { return $true }
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $norm = {
        param($s)
        $d = $s.Normalize([Text.NormalizationForm]::FormD)
        $sb = New-Object Text.StringBuilder
        foreach ($ch in $d.ToCharArray()) {
            if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch) -ne
                [Globalization.UnicodeCategory]::NonSpacingMark) { [void]$sb.Append($ch) }
        }
        # d/D co gach khong phan ra duoc bang FormD nen phai thay tay.
        $sb.ToString().Replace([char]0x0111, 'd').Replace([char]0x0110, 'D').ToLowerInvariant()
    }
    $c = & $norm $Candidate
    $p = & $norm $Pattern
    if ($Exact) { return $c -eq $p }
    return $c.Contains($p)
}

function Walk {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [int]$Level,
        [int]$MaxLevel,
        [scriptblock]$Visit
    )
    if ($null -eq $Element -or $Level -gt $MaxLevel) { return }
    & $Visit $Element $Level
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        Walk -Element $child -Level ($Level + 1) -MaxLevel $MaxLevel -Visit $Visit
        $child = $walker.GetNextSibling($child)
    }
}

$root = Get-RevitRoot -TitleLike $WindowTitleLike
if ($null -eq $root) { throw "Khong tim thay cua so khop '$WindowTitleLike'." }
Write-Output ("ROOT=" + $root.Current.Name)

$hits = New-Object System.Collections.ArrayList

Walk -Element $root -Level 0 -MaxLevel $Depth -Visit {
    param($el, $lvl)
    try {
        $n = $el.Current.Name
        $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($Action -eq 'Dump') {
            Write-Output (('  ' * $lvl) + "[$ct] '$n'")
            return
        }
        if (Test-NameMatch -Candidate $n -Pattern $Name -Exact:$ExactName.IsPresent) {
            [void]$hits.Add([pscustomobject]@{ Element = $el; Name = $n; ControlType = $ct; Level = $lvl })
        }
    } catch {
        # Control bien mat giua chung khi cay dang doi - bo qua, khong lam hong ca luot quet.
    }
}

if ($Action -eq 'Dump') { return }

if ($hits.Count -eq 0) { Write-Output "NO_MATCH name='$Name'"; exit 2 }

foreach ($h in $hits) { Write-Output ("HIT lvl=" + $h.Level + " [" + $h.ControlType + "] '" + $h.Name + "'") }

if ($Action -eq 'Find') { exit 0 }

if ($Action -eq 'Click') {
    # Nut tren ribbon Revit la Custom, khong co InvokePattern. Duong con lai la bam that theo toa do
    # do chinh control bao ve - van khong phai doan pixel.
    $target = $hits | Where-Object { $_.ControlType -in @('Custom', 'Button', 'MenuItem', 'ListItem') }
    if ($target.Count -eq 0) { $target = $hits }
    if ($target.Count -gt 1) { Write-Output ("AMBIGUOUS count=" + $target.Count); exit 3 }

    $el = $target[0].Element
    $rect = $el.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "Control '$($target[0].Name)' khong co vung hien thi." }
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeMouse
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
'@
    [NativeMouse]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 250
    [NativeMouse]::mouse_event([NativeMouse]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [NativeMouse]::mouse_event([NativeMouse]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Output ("CLICKED '" + $target[0].Name.Replace("`n", ' / ') + "' at $x,$y")
    exit 0
}

# --- Invoke ---
# 'Custom' phai co: nut tren ribbon Revit lo ra duoi dang Custom chu khong phai Button.
$clickable = $hits | Where-Object { $_.ControlType -in @('Button', 'MenuItem', 'ListItem', 'TabItem', 'RadioButton', 'CheckBox', 'SplitButton', 'Custom') }
if ($clickable.Count -eq 0) { $clickable = $hits }
if ($clickable.Count -gt 1) {
    Write-Output ("AMBIGUOUS count=" + $clickable.Count + " - siet them -Name hoac dung -ExactName")
    exit 3
}

$target = $clickable[0].Element
$patterns = @(
    @{ Id = [System.Windows.Automation.InvokePattern]::Pattern;         Run = { param($p) $p.Invoke() } },
    @{ Id = [System.Windows.Automation.SelectionItemPattern]::Pattern;  Run = { param($p) $p.Select() } },
    @{ Id = [System.Windows.Automation.TogglePattern]::Pattern;         Run = { param($p) $p.Toggle() } }
)
foreach ($entry in $patterns) {
    $pattern = $null
    if ($target.TryGetCurrentPattern($entry.Id, [ref]$pattern)) {
        & $entry.Run $pattern
        Write-Output ("INVOKED '" + $clickable[0].Name + "' via " + $entry.Id.ProgrammaticName)
        exit 0
    }
}

throw ("Control '" + $clickable[0].Name + "' khong ho tro pattern bam nao.")
