param(
    [Parameter(Mandatory = $true)][long]$Handle,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$Delta
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WindowScroller
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
$rect = New-Object WindowScroller+RECT
if (-not [WindowScroller]::GetWindowRect([IntPtr]$Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
[void][WindowScroller]::SetForegroundWindow([IntPtr]$Handle)
[void][WindowScroller]::SetCursorPos($rect.Left + $X, $rect.Top + $Y)
Start-Sleep -Milliseconds 150
$wheelData = [BitConverter]::ToUInt32([BitConverter]::GetBytes($Delta), 0)
[WindowScroller]::mouse_event(0x0800, 0, 0, $wheelData, [UIntPtr]::Zero)
Write-Output "SCROLLED=$Delta"
