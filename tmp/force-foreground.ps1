<#
.SYNOPSIS
    Ep mot cua so len foreground that su, vuot qua khoa doi foreground cua Windows.

.DESCRIPTION
    AppActivate that bai khi tien trinh goi khong giu foreground. Cach dung duoc la gan tam thoi
    input queue cua luong hien tai vao luong dang giu foreground (AttachThreadInput) roi moi goi
    SetForegroundWindow. Bat buoc phai co truoc khi doc BoundingRectangle cua control tren ribbon:
    ribbon chua render thi UIA tra ve toa do ngoai man hinh (am hang chuc nghin).
#>
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Fg
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public const int SW_RESTORE = 9;
}
'@

$proc = Get-Process -Id $ProcessId -ErrorAction Stop
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "Process $ProcessId khong co main window." }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    [Fg]::ShowWindow($hwnd, [Fg]::SW_RESTORE) | Out-Null
    [Fg]::BringWindowToTop($hwnd) | Out-Null

    $fgWnd = [Fg]::GetForegroundWindow()
    $fgThread = [Fg]::GetWindowThreadProcessId($fgWnd, [ref]([uint32]0))
    $myThread = [Fg]::GetCurrentThreadId()

    $attached = $false
    if ($fgThread -ne 0 -and $fgThread -ne $myThread) {
        $attached = [Fg]::AttachThreadInput($myThread, $fgThread, $true)
    }
    [Fg]::SetForegroundWindow($hwnd) | Out-Null
    if ($attached) { [Fg]::AttachThreadInput($myThread, $fgThread, $false) | Out-Null }

    Start-Sleep -Milliseconds 400
    if ([Fg]::GetForegroundWindow() -eq $hwnd) {
        Write-Output "FOREGROUND_OK pid=$ProcessId"
        exit 0
    }
} while ((Get-Date) -lt $deadline)

throw "Khong dua duoc pid=$ProcessId len foreground trong $TimeoutSeconds giay."
