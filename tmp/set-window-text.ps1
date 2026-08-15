param(
    [Parameter(Mandatory = $true)][long]$Handle,
    [Parameter(Mandatory = $true)][string]$Text
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeText
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
}
'@

$WM_SETTEXT = 0x000C
[NativeText]::SendMessage([IntPtr]$Handle, $WM_SETTEXT, [IntPtr]::Zero, $Text) | Out-Null
Write-Output "TEXT_SET=$Handle"
