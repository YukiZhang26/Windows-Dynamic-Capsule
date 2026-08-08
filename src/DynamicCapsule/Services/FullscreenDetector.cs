using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DynamicCapsule.Services;

internal sealed record FullscreenSnapshot(
    bool IsFullscreen,
    nint WindowHandle,
    nint MonitorHandle,
    string WindowClass)
{
    internal static readonly FullscreenSnapshot None =
        new(false, nint.Zero, nint.Zero, string.Empty);
}

internal sealed class FullscreenDetector : IDisposable
{
    private const int EdgeToleranceInPixels = 2;
    private const long WsExToolWindow = 0x00000080L;

    private static readonly HashSet<string> ExcludedWindowClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd"
        };

    private readonly nint _ownWindowHandle;
    private readonly DispatcherTimer _pollTimer;

    private FullscreenSnapshot _current = FullscreenSnapshot.None;
    private bool _isRunning;
    private bool _disposed;

    internal FullscreenDetector(nint ownWindowHandle)
    {
        _ownWindowHandle = ownWindowHandle;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _pollTimer.Tick += OnPollTimerTick;
    }

    internal event Action<FullscreenSnapshot>? StateChanged;

    internal FullscreenSnapshot Current => _current;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        Refresh();
        _pollTimer.Start();
    }

    internal void Stop()
    {
        if (_disposed || !_isRunning)
        {
            return;
        }

        _isRunning = false;
        _pollTimer.Stop();
    }

    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var next = DetectFullscreenWindow();
        if (next == _current)
        {
            return;
        }

        _current = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isRunning = false;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
    }

    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        Refresh();
    }

    private FullscreenSnapshot DetectFullscreenWindow()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == nint.Zero
            || foregroundWindow == _ownWindowHandle
            || !NativeMethods.IsWindowVisible(foregroundWindow)
            || NativeMethods.IsIconic(foregroundWindow))
        {
            return FullscreenSnapshot.None;
        }

        var windowClass = GetWindowClass(foregroundWindow);
        if (ExcludedWindowClasses.Contains(windowClass))
        {
            var cursorMonitor = NativeMethods.GetCursorPos(out var cursorPosition)
                ? NativeMethods.MonitorFromPoint(
                    cursorPosition,
                    NativeMethods.MonitorDefaultToNearest)
                : nint.Zero;

            return new FullscreenSnapshot(
                false,
                foregroundWindow,
                cursorMonitor,
                windowClass);
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(
            foregroundWindow,
            NativeMethods.GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0)
        {
            return FullscreenSnapshot.None;
        }

        if (IsWindowCloaked(foregroundWindow))
        {
            return FullscreenSnapshot.None;
        }

        var monitorHandle = NativeMethods.MonitorFromWindow(
            foregroundWindow,
            NativeMethods.MonitorDefaultToNearest);
        if (monitorHandle == nint.Zero)
        {
            return FullscreenSnapshot.None;
        }

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return FullscreenSnapshot.None;
        }

        var isFullscreen = TryGetVisibleWindowBounds(
                               foregroundWindow,
                               out var windowBounds)
                           && CoversMonitor(windowBounds, monitorInfo.Monitor);

        return new FullscreenSnapshot(
            isFullscreen,
            foregroundWindow,
            monitorHandle,
            windowClass);
    }

    private static bool TryGetVisibleWindowBounds(
        nint windowHandle,
        out NativeMethods.Rect bounds)
    {
        var result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out bounds,
            Marshal.SizeOf<NativeMethods.Rect>());

        return result >= 0 || NativeMethods.GetWindowRect(windowHandle, out bounds);
    }

    private static bool IsWindowCloaked(nint windowHandle)
    {
        int cloaked;
        var result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaCloaked,
            out cloaked,
            Marshal.SizeOf<int>());

        return result >= 0 && cloaked != 0;
    }

    private static bool CoversMonitor(
        NativeMethods.Rect windowBounds,
        NativeMethods.Rect monitorBounds)
    {
        return windowBounds.Left <= monitorBounds.Left + EdgeToleranceInPixels
               && windowBounds.Top <= monitorBounds.Top + EdgeToleranceInPixels
               && windowBounds.Right >= monitorBounds.Right - EdgeToleranceInPixels
               && windowBounds.Bottom >= monitorBounds.Bottom - EdgeToleranceInPixels
               && windowBounds.Width >= monitorBounds.Width - (EdgeToleranceInPixels * 2)
               && windowBounds.Height >= monitorBounds.Height - (EdgeToleranceInPixels * 2);
    }

    private static string GetWindowClass(nint windowHandle)
    {
        var buffer = new StringBuilder(256);
        return NativeMethods.GetClassName(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }
}
