param([Parameter(Mandatory = $true)][int]$ProcessId)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
[Microsoft.VisualBasic.Interaction]::AppActivate($ProcessId) | Out-Null
[System.Windows.Forms.SendKeys]::SendWait('%f')
Start-Sleep -Milliseconds 300
$process = Get-Process -Id $ProcessId
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$arrow = $null
foreach ($element in $all) {
    if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $element.Current.AutomationId -eq 'ID_REVIT_FILE_SAVEAS_ArrowButton') {
        $arrow = $element
        break
    }
}
if ($null -eq $arrow) { throw 'Save As arrow not found.' }
([System.Windows.Automation.InvokePattern]$arrow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Milliseconds 300
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$family = $null
foreach ($element in $all) {
    if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $element.Current.AutomationId -eq 'ID_REVIT_SAVE_AS_FAMILY_2NDMenuCommandButton') {
        $family = $element
        break
    }
}
if ($null -eq $family) { throw 'Save As Family command not found.' }
([System.Windows.Automation.InvokePattern]$family.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output 'SAVE_AS_FAMILY_OPENED'
