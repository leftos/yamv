<#
.SYNOPSIS
    Saves a screenshot of a process's main window as a PNG, without showing or focusing anything.

.DESCRIPTION
    Finds the largest visible top-level window of the process with the given id and has the window paint itself into a bitmap with
    PrintWindow(PW_RENDERFULLCONTENT), which includes the WebView2 content of the document page. Nothing is brought
    to the front and the screen is never read, so it also works on a desktop nobody looks at.

    Run it on the same desktop as the window, since a process only sees the windows of its own desktop. To keep
    the harness off the user's screen, start both under tools/hidden-desktop.ps1, for example a small script that
    launches yamv.exe, waits, calls this script and stops yamv.exe:

        pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File <that script>

.PARAMETER ProcessId
    The id of the process whose main window to capture.

.PARAMETER Out
    The PNG file to write. Its folder is created when missing.

.PARAMETER TimeoutSeconds
    How long to wait for the window to appear. Default 15.

.PARAMETER WithPopups
    Also paint the process's other visible top-level windows that overlap the main one (a popup such as the content-width
    popover is a window of its own) over the capture, each where it sits on the screen.

.EXAMPLE
    pwsh tools/dev/capture-window.ps1 -ProcessId 1234 -Out .tmp/shot.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $Out,
    [int] $TimeoutSeconds = 15,
    [switch] $WithPopups
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace YamvCapture -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdc, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint processId);
public delegate bool EnumWindowsProc(System.IntPtr hwnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, System.IntPtr lParam);

// The process's largest visible top-level window. Process.MainWindowHandle can pick a small helper window instead.
public static System.IntPtr LargestWindow(uint processId)
{
    System.IntPtr best = System.IntPtr.Zero;
    long bestArea = 0;
    EnumWindows((hwnd, _) =>
    {
        uint owner;
        GetWindowThreadProcessId(hwnd, out owner);
        RECT r;
        if (owner == processId && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out r))
        {
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
        }
        return true;
    }, System.IntPtr.Zero);
    return best;
}

// The process's other visible top-level windows, topmost first, so painting them in reverse keeps the z-order.
public static System.Collections.Generic.List<System.IntPtr> OtherWindows(uint processId, System.IntPtr main)
{
    var found = new System.Collections.Generic.List<System.IntPtr>();
    EnumWindows((hwnd, _) =>
    {
        uint owner;
        GetWindowThreadProcessId(hwnd, out owner);
        if (owner == processId && hwnd != main && IsWindowVisible(hwnd)) { found.Add(hwnd); }
        return true;
    }, System.IntPtr.Zero);
    return found;
}
'@

$PW_RENDERFULLCONTENT = 2

[void][YamvCapture.Native]::SetProcessDPIAware()

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$handle = [IntPtr]::Zero
while ([DateTime]::UtcNow -lt $deadline) {
    [void](Get-Process -Id $ProcessId)
    $handle = [YamvCapture.Native]::LargestWindow([uint32]$ProcessId)
    if ($handle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
if ($handle -eq [IntPtr]::Zero) {
    throw "Process $ProcessId has no main window on this desktop after $TimeoutSeconds s."
}

$rect = New-Object YamvCapture.Native+RECT
if (-not [YamvCapture.Native]::GetWindowRect($handle, [ref] $rect)) {
    throw "GetWindowRect failed with Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) for process $ProcessId."
}
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Window of process $ProcessId has an empty rectangle ($width x $height)."
}

$outPath = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
$folder = [IO.Path]::GetDirectoryName($outPath)
if (-not (Test-Path -LiteralPath $folder)) {
    New-Item -ItemType Directory -Path $folder | Out-Null
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [YamvCapture.Native]::PrintWindow($handle, $hdc, $PW_RENDERFULLCONTENT)) {
                throw "PrintWindow failed with Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) for process $ProcessId."
            }
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }
    }
    finally {
        $graphics.Dispose()
    }
    if ($WithPopups) {
        $others = [YamvCapture.Native]::OtherWindows([uint32]$ProcessId, $handle)
        for ($i = $others.Count - 1; $i -ge 0; $i--) {
            $popup = $others[$i]
            $popupRect = New-Object YamvCapture.Native+RECT
            if (-not [YamvCapture.Native]::GetWindowRect($popup, [ref] $popupRect)) {
                continue
            }
            $popupWidth = $popupRect.Right - $popupRect.Left
            $popupHeight = $popupRect.Bottom - $popupRect.Top
            $overlaps = $popupRect.Left -lt $rect.Right -and $popupRect.Right -gt $rect.Left -and $popupRect.Top -lt $rect.Bottom -and $popupRect.Bottom -gt $rect.Top
            if ($popupWidth -le 0 -or $popupHeight -le 0 -or -not $overlaps) {
                continue
            }
            $popupBitmap = New-Object System.Drawing.Bitmap $popupWidth, $popupHeight
            try {
                $popupGraphics = [System.Drawing.Graphics]::FromImage($popupBitmap)
                try {
                    $popupHdc = $popupGraphics.GetHdc()
                    try {
                        if (-not [YamvCapture.Native]::PrintWindow($popup, $popupHdc, $PW_RENDERFULLCONTENT)) {
                            throw "PrintWindow failed with Win32 error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()) for a popup of process $ProcessId."
                        }
                    }
                    finally {
                        $popupGraphics.ReleaseHdc($popupHdc)
                    }
                }
                finally {
                    $popupGraphics.Dispose()
                }
                $target = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $target.DrawImage($popupBitmap, $popupRect.Left - $rect.Left, $popupRect.Top - $rect.Top, $popupWidth, $popupHeight)
                }
                finally {
                    $target.Dispose()
                }
                Write-Output "Painted a $popupWidth x $popupHeight popup at ($($popupRect.Left - $rect.Left), $($popupRect.Top - $rect.Top))"
            }
            finally {
                $popupBitmap.Dispose()
            }
        }
    }
    $bitmap.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}

Write-Output "Saved $width x $height capture of process $ProcessId to $outPath"
