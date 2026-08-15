param(
    [Parameter(Mandatory = $true)][long]$WindowHandle,
    [Parameter(Mandatory = $true)][long]$ControlHandle,
    [Parameter(Mandatory = $true)][string]$Text
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeFocusPaste
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@

$rect = New-Object NativeFocusPaste+RECT
if (-not [NativeFocusPaste]::GetWindowRect([IntPtr]$ControlHandle, [ref]$rect)) { throw 'GetWindowRect failed.' }
[void][NativeFocusPaste]::SetForegroundWindow([IntPtr]$WindowHandle)
[void][NativeFocusPaste]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2))
[NativeFocusPaste]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[NativeFocusPaste]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[System.Windows.Forms.Clipboard]::SetText($Text)
[System.Windows.Forms.SendKeys]::SendWait('^a')
[System.Windows.Forms.SendKeys]::SendWait('^v')
Write-Output "PASTED=$ControlHandle"
