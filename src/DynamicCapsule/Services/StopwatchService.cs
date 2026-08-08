using DynamicCapsule.Models;

namespace DynamicCapsule.Services;

internal sealed class StopwatchService : IDisposable
{
    private static readonly TimeSpan TickInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _timer;

    private string? _eventId;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _createdAt;
    private TimeSpan _accumulated;
    private long _lastPublishedSecond = -1;
    private bool _isPaused;
    private bool _disposed;

    internal StopwatchService()
    {
        _timer = new System.Threading.Timer(
            _ => OnTick(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal event Action<CapsuleEvent>? EventChanged;
    internal event Action<StopwatchStatus>? StatusChanged;

    internal StopwatchStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return CreateStatus(DateTimeOffset.UtcNow);
            }
        }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var now = DateTimeOffset.UtcNow;
        CapsuleEvent? replacedEvent = null;
        CapsuleEvent runningEvent;
        StopwatchStatus status;
        lock (_gate)
        {
            if (_eventId is not null)
            {
                replacedEvent = CreateTerminalEvent(
                    "已由新秒表替换",
                    now,
                    TimeSpan.FromSeconds(2));
            }

            _eventId = $"stopwatch:{Guid.NewGuid():N}";
            _startedAt = now;
            _createdAt = now;
            _accumulated = TimeSpan.Zero;
            _lastPublishedSecond = 0;
            _isPaused = false;
            runningEvent = CreateRunningEvent(now);
            status = CreateStatus(now);
            _timer.Change(TickInterval, TickInterval);
        }

        if (replacedEvent is not null)
        {
            EventChanged?.Invoke(replacedEvent);
        }

        EventChanged?.Invoke(runningEvent);
        StatusChanged?.Invoke(status);
    }

    internal void TogglePause()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        CapsuleEvent? stopwatchEvent = null;
        StopwatchStatus? status = null;
        lock (_gate)
        {
            if (_eventId is null)
            {
                return;
            }

            if (_isPaused)
            {
                _startedAt = now;
                _isPaused = false;
            }
            else
            {
                _accumulated = GetElapsed(now);
                _isPaused = true;
            }

            _lastPublishedSecond = GetDisplaySeconds(GetElapsed(now));
            stopwatchEvent = CreateRunningEvent(now);
            status = CreateStatus(now);
        }

        EventChanged?.Invoke(stopwatchEvent);
        StatusChanged?.Invoke(status);
    }

    internal void Stop()
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? terminalEvent = null;
        lock (_gate)
        {
            if (_eventId is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _accumulated = GetElapsed(now);
            terminalEvent = CreateTerminalEvent(
                $"秒表已结束 · {FormatElapsed(_accumulated)}",
                now,
                TimeSpan.FromSeconds(4));
            ResetActiveStopwatch();
        }

        EventChanged?.Invoke(terminalEvent);
        StatusChanged?.Invoke(StopwatchStatus.Inactive);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            ResetActiveStopwatch();
        }

        _timer.Dispose();
    }

    private void OnTick()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        CapsuleEvent? stopwatchEvent = null;
        StopwatchStatus? status = null;
        lock (_gate)
        {
            if (_eventId is null || _isPaused)
            {
                return;
            }

            var elapsed = GetElapsed(now);
            var displaySecond = GetDisplaySeconds(elapsed);
            if (displaySecond == _lastPublishedSecond)
            {
                return;
            }

            _lastPublishedSecond = displaySecond;
            stopwatchEvent = CreateRunningEvent(now);
            status = CreateStatus(now);
        }

        EventChanged?.Invoke(stopwatchEvent);
        StatusChanged?.Invoke(status);
    }

    private CapsuleEvent CreateRunningEvent(DateTimeOffset now)
    {
        var elapsed = GetElapsed(now);
        var displayElapsed = FormatElapsed(elapsed);
        return new CapsuleEvent(
            _eventId!,
            CapsuleEventKind.Stopwatch,
            CapsuleEventPriority.Normal,
            "stopwatch",
            "秒表",
            _isPaused ? "秒表（已暂停）" : "秒表",
            _isPaused
                ? $"已暂停 · {displayElapsed}"
                : $"已计时 {displayElapsed}",
            (elapsed.TotalSeconds % 60) / 60,
            CapsuleTaskState.Running,
            EventPrivacyLevel.Full,
            _createdAt,
            null);
    }

    private CapsuleEvent CreateTerminalEvent(
        string message,
        DateTimeOffset now,
        TimeSpan visibleDuration)
    {
        return new CapsuleEvent(
            _eventId!,
            CapsuleEventKind.Stopwatch,
            CapsuleEventPriority.Normal,
            "stopwatch",
            "秒表",
            "秒表",
            message,
            null,
            CapsuleTaskState.Succeeded,
            EventPrivacyLevel.Full,
            _createdAt,
            now + visibleDuration);
    }

    private StopwatchStatus CreateStatus(DateTimeOffset now)
    {
        return _eventId is null
            ? StopwatchStatus.Inactive
            : new StopwatchStatus(
                true,
                _isPaused,
                _eventId,
                GetElapsed(now));
    }

    private TimeSpan GetElapsed(DateTimeOffset now)
    {
        return _isPaused
            ? _accumulated
            : _accumulated + (now - _startedAt);
    }

    private void ResetActiveStopwatch()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eventId = null;
        _startedAt = default;
        _createdAt = default;
        _accumulated = TimeSpan.Zero;
        _lastPublishedSecond = -1;
        _isPaused = false;
    }

    private static long GetDisplaySeconds(TimeSpan elapsed)
    {
        return Math.Max(0, (long)Math.Floor(elapsed.TotalSeconds));
    }

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)Math.Floor(elapsed.TotalSeconds));
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var remainingSeconds = seconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }
}
