param(
    [Parameter(Mandatory = $true)][long]$Handle,
    [string]$NamePattern = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
if ($null -eq $root) { throw "Cannot inspect window $Handle." }
$all = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
foreach ($item in $all) {
    $name = $item.Current.Name
    $id = $item.Current.AutomationId
    if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($id)) { continue }
    if (-not [string]::IsNullOrWhiteSpace($NamePattern) -and
        $name -notmatch $NamePattern -and $id -notmatch $NamePattern) { continue }
    $rect = $item.Current.BoundingRectangle
    [pscustomobject]@{
        Type = $item.Current.ControlType.ProgrammaticName
        Name = $name
        AutomationId = $id
        Enabled = $item.Current.IsEnabled
        Offscreen = $item.Current.IsOffscreen
        X = [math]::Round($rect.X)
        Y = [math]::Round($rect.Y)
        Width = [math]::Round($rect.Width)
        Height = [math]::Round($rect.Height)
    }
}
