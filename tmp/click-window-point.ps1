param(
    [Parameter(Mandatory = $true)][long]$Handle,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WindowPointClick
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@

$rect = New-Object WindowPointClick+RECT
if (-not [WindowPointClick]::GetWindowRect([IntPtr]$Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
[void][WindowPointClick]::SetForegroundWindow([IntPtr]$Handle)
Start-Sleep -Milliseconds 100
$screenX = $rect.Left + $X
$screenY = $rect.Top + $Y
[void][WindowPointClick]::SetCursorPos($screenX, $screenY)
[WindowPointClick]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[WindowPointClick]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Write-Output "CLICKED=$screenX,$screenY"
