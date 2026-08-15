param(
    [Parameter(Mandatory = $true)][long]$Handle
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WindowActivator
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
'@

$target = [IntPtr]$Handle
$foreground = [WindowActivator]::GetForegroundWindow()
$unusedPid = [uint32]0
$foregroundThread = [WindowActivator]::GetWindowThreadProcessId($foreground, [ref]$unusedPid)
$targetThread = [WindowActivator]::GetWindowThreadProcessId($target, [ref]$unusedPid)
$currentThread = [WindowActivator]::GetCurrentThreadId()

[void][WindowActivator]::AttachThreadInput($currentThread, $foregroundThread, $true)
[void][WindowActivator]::AttachThreadInput($currentThread, $targetThread, $true)
try {
    [void][WindowActivator]::ShowWindowAsync($target, 9)
    [void][WindowActivator]::SetWindowPos($target, [IntPtr](-1), 0, 0, 0, 0, 0x0043)
    [void][WindowActivator]::SetWindowPos($target, [IntPtr](-2), 0, 0, 0, 0, 0x0043)
    [void][WindowActivator]::BringWindowToTop($target)
    [void][WindowActivator]::SetForegroundWindow($target)
    [void][WindowActivator]::SetFocus($target)
}
finally {
    [void][WindowActivator]::AttachThreadInput($currentThread, $targetThread, $false)
    [void][WindowActivator]::AttachThreadInput($currentThread, $foregroundThread, $false)
}

Start-Sleep -Milliseconds 400
if ([WindowActivator]::GetForegroundWindow().ToInt64() -ne $target.ToInt64()) {
    throw "Failed to activate window $Handle."
}
Write-Output "ACTIVATED=$Handle"
