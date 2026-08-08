param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$DotnetRoot,

    [Parameter(Mandatory = $true)]
    [string]$ScreenshotPath
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class CapsuleWindowProbe
{
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    public static IntPtr FindVisibleWindowForProcess(int processId)
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((windowHandle, parameter) =>
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(windowHandle, out ownerProcessId);
            if (ownerProcessId == processId && IsWindowVisible(windowHandle))
            {
                result = windowHandle;
                return false;
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

[CapsuleWindowProbe]::EnablePerMonitorDpi() | Out-Null

$foregroundBefore = [CapsuleWindowProbe]::GetForegroundWindow()
$previousDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')

try {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $DotnetRoot, 'Process')
    $process = Start-Process -FilePath $ExecutablePath -PassThru
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $previousDotnetRoot, 'Process')
}

$deadline = [DateTime]::UtcNow.AddSeconds(8)
$windowHandle = [IntPtr]::Zero

do {
    Start-Sleep -Milliseconds 100
    $process.Refresh()

    if ($process.HasExited) {
        throw "Capsule process exited with code $($process.ExitCode)."
    }

    $windowHandle = [CapsuleWindowProbe]::FindVisibleWindowForProcess($process.Id)
} while ($windowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

if ($windowHandle -eq [IntPtr]::Zero) {
    throw 'Capsule window did not become visible within 8 seconds.'
}

Start-Sleep -Milliseconds 300

$foregroundAfter = [CapsuleWindowProbe]::GetForegroundWindow()
$extendedStyle = [CapsuleWindowProbe]::GetWindowLongPtr($windowHandle, -20).ToInt64()

$rect = New-Object CapsuleWindowProbe+Rect
if (-not [CapsuleWindowProbe]::GetWindowRect($windowHandle, [ref]$rect)) {
    throw 'Unable to read the capsule window rectangle.'
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

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

    $bitmap.Save($ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

[PSCustomObject]@{
    ProcessId = $process.Id
    WindowHandle = $windowHandle.ToInt64()
    ForegroundUnchanged = $foregroundBefore -eq $foregroundAfter
    HasNoActivateStyle = ($extendedStyle -band 0x08000000) -ne 0
    HasToolWindowStyle = ($extendedStyle -band 0x00000080) -ne 0
    Left = $rect.Left
    Top = $rect.Top
    Width = $width
    Height = $height
    Screenshot = $ScreenshotPath
} | ConvertTo-Json
