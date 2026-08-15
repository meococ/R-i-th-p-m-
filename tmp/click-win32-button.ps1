param([Parameter(Mandatory = $true)][long]$Handle)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeButton
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@

$BM_CLICK = 0x00F5
[NativeButton]::SendMessage([IntPtr]$Handle, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Write-Output "CLICKED=$Handle"
