[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int] $ProcessId,

    [Parameter(Mandatory = $true)]
    [string] $ScreenshotPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RunningCapsuleWindowProbe
{
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    private delegate bool EnumWindowsCallback(
        IntPtr windowHandle,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr windowHandle,
        out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(
        IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    public static IntPtr FindVisibleWindowForProcess(int processId)
    {
        var result = IntPtr.Zero;
        long largestArea = 0;
        EnumWindows((windowHandle, parameter) =>
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(windowHandle, out ownerProcessId);
            if (ownerProcessId == processId && IsWindowVisible(windowHandle))
            {
                Rect rect;
                if (GetWindowRect(windowHandle, out rect))
                {
                    long width = Math.Max(0, rect.Right - rect.Left);
                    long height = Math.Max(0, rect.Bottom - rect.Top);
                    long area = width * height;
                    if (area > largestArea)
                    {
                        largestArea = area;
                        result = windowHandle;
                    }
                }
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool EnablePerMonitorDpi()
    {
        return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }
}
'@

[RunningCapsuleWindowProbe]::EnablePerMonitorDpi() | Out-Null

$windowHandle =
    [RunningCapsuleWindowProbe]::FindVisibleWindowForProcess($ProcessId)
if ($windowHandle -eq [IntPtr]::Zero) {
    throw "No visible window was found for process $ProcessId."
}

$rect = New-Object RunningCapsuleWindowProbe+Rect
if (-not [RunningCapsuleWindowProbe]::GetWindowRect(
        $windowHandle,
        [ref] $rect)) {
    throw "Unable to read the capsule window rectangle."
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "The capsule window has an invalid rectangle."
}

$screenshotDirectory = Split-Path -Parent $ScreenshotPath
if (-not [string]::IsNullOrWhiteSpace($screenshotDirectory)) {
    New-Item `
        -ItemType Directory `
        -Path $screenshotDirectory `
        -Force | Out-Null
}

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen(
        $rect.Left,
        $rect.Top,
        0,
        0,
        (New-Object System.Drawing.Size($width, $height)))
    $bitmap.Save(
        $ScreenshotPath,
        [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

[PSCustomObject]@{
    ProcessId = $ProcessId
    WindowHandle = $windowHandle.ToInt64()
    Left = $rect.Left
    Top = $rect.Top
    Width = $width
    Height = $height
    Screenshot = $ScreenshotPath
}
