param([Parameter(Mandatory = $true)][long]$Handle)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ChildWindowProbe
{
    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumChildProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@

$BM_GETCHECK = 0x00F0
[ChildWindowProbe]::EnumChildWindows([IntPtr]$Handle, {
    param($child, $unused)
    $title = New-Object System.Text.StringBuilder 1024
    $class = New-Object System.Text.StringBuilder 256
    [void][ChildWindowProbe]::GetWindowText($child, $title, $title.Capacity)
    [void][ChildWindowProbe]::GetClassName($child, $class, $class.Capacity)
    $check = [ChildWindowProbe]::SendMessage($child, $BM_GETCHECK, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    [pscustomobject]@{
        Handle = $child.ToInt64()
        Id = [ChildWindowProbe]::GetDlgCtrlID($child)
        Visible = [ChildWindowProbe]::IsWindowVisible($child)
        Enabled = [ChildWindowProbe]::IsWindowEnabled($child)
        Class = $class.ToString()
        Title = $title.ToString()
        Check = $check
    } | Format-List | Out-Host
    return $true
}, [IntPtr]::Zero) | Out-Null
