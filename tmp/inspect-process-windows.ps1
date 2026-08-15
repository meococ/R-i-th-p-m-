param([Parameter(Mandatory = $true)][int]$ProcessId)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WindowProbe
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
}
'@

[WindowProbe]::EnumWindows({
    param($handle, $unused)
    [uint32]$owner = 0
    [void][WindowProbe]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($owner -ne $ProcessId) { return $true }
    $title = New-Object System.Text.StringBuilder 2048
    $class = New-Object System.Text.StringBuilder 512
    [void][WindowProbe]::GetWindowText($handle, $title, $title.Capacity)
    [void][WindowProbe]::GetClassName($handle, $class, $class.Capacity)
    [pscustomobject]@{
        Handle = $handle.ToInt64()
        Visible = [WindowProbe]::IsWindowVisible($handle)
        Enabled = [WindowProbe]::IsWindowEnabled($handle)
        Class = $class.ToString()
        Title = $title.ToString()
    } | Format-List | Out-Host
    return $true
}, [IntPtr]::Zero) | Out-Null
