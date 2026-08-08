$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class MonitorTopologyProbe
{
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    public delegate bool MonitorEnumCallback(
        IntPtr monitorHandle,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(
        IntPtr monitorHandle,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    public static bool EnablePerMonitorDpi()
    {
        return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }
}
'@

[MonitorTopologyProbe]::EnablePerMonitorDpi() | Out-Null

$monitors = [System.Collections.Generic.List[object]]::new()
$callback = [MonitorTopologyProbe+MonitorEnumCallback]{
    param($monitorHandle, $deviceContext, $monitorRectangle, $parameter)

    $monitorInfo = New-Object MonitorTopologyProbe+MonitorInfo
    $monitorInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf(
        [type][MonitorTopologyProbe+MonitorInfo])

    if (-not [MonitorTopologyProbe]::GetMonitorInfo(
            $monitorHandle,
            [ref]$monitorInfo)) {
        return $true
    }

    [uint32]$dpiX = 96
    [uint32]$dpiY = 96
    [MonitorTopologyProbe]::GetDpiForMonitor(
        $monitorHandle,
        0,
        [ref]$dpiX,
        [ref]$dpiY) | Out-Null

    $scale = $dpiX / 96.0
    $workWidth = $monitorInfo.Work.Right - $monitorInfo.Work.Left
    $workHeight = $monitorInfo.Work.Bottom - $monitorInfo.Work.Top

    $layouts = foreach ($layout in @(
            @{ Name = 'Compact'; Width = 136; Height = 36 },
            @{ Name = 'MediaCompact'; Width = 250; Height = 36 },
            @{ Name = 'Expanded'; Width = 460; Height = 120 }
        )) {
        $physicalWidth = [Math]::Max(
            1,
            [int][Math]::Round($layout.Width * $scale))
        $physicalHeight = [Math]::Max(
            1,
            [int][Math]::Round($layout.Height * $scale))

        [PSCustomObject]@{
            Name = $layout.Name
            Left = $monitorInfo.Work.Left + [int]((
                    $workWidth - $physicalWidth) / 2)
            Top = $monitorInfo.Work.Top + [int][Math]::Round(10 * $scale)
            Width = $physicalWidth
            Height = $physicalHeight
        }
    }

    $monitors.Add([PSCustomObject]@{
            Handle = $monitorHandle.ToInt64()
            DeviceName = $monitorInfo.DeviceName
            IsPrimary = ($monitorInfo.Flags -band 1) -ne 0
            MonitorBounds = "$($monitorInfo.Monitor.Left),$($monitorInfo.Monitor.Top),$($monitorInfo.Monitor.Right),$($monitorInfo.Monitor.Bottom)"
            WorkBounds = "$($monitorInfo.Work.Left),$($monitorInfo.Work.Top),$($monitorInfo.Work.Right),$($monitorInfo.Work.Bottom)"
            WorkSize = "$workWidth x $workHeight"
            Dpi = "$dpiX x $dpiY"
            ScalePercent = [int][Math]::Round($scale * 100)
            Layouts = $layouts
        })

    return $true
}

if (-not [MonitorTopologyProbe]::EnumDisplayMonitors(
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        $callback,
        [IntPtr]::Zero)) {
    throw 'Unable to enumerate display monitors.'
}

[PSCustomObject]@{
    MonitorCount = $monitors.Count
    HasMixedDpi = ($monitors.ScalePercent | Select-Object -Unique).Count -gt 1
    Monitors = $monitors
} | ConvertTo-Json -Depth 5
