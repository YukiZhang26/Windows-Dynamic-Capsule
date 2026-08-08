using DynamicCapsule.Models;

namespace DynamicCapsule.Services;

internal sealed class CountdownTimerService : IDisposable
{
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _timer;

    private string? _eventId;
    private string _title = string.Empty;
    private TimeSpan _duration;
    private TimeSpan _pausedRemaining;
    private DateTimeOffset _deadline;
    private DateTimeOffset _createdAt;
    private long _lastPublishedSecond = -1;
    private bool _isPaused;
    private bool _disposed;

    internal CountdownTimerService()
    {
        _timer = new System.Threading.Timer(
            _ => OnTick(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal event Action<CapsuleEvent>? EventChanged;
    internal event Action<CountdownTimerStatus>? StatusChanged;

    internal CountdownTimerStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return CreateStatus(DateTimeOffset.UtcNow);
            }
        }
    }

    internal void Start(TimeSpan duration, string? title = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        duration = duration < MinimumDuration
            ? MinimumDuration
            : duration > MaximumDuration
                ? MaximumDuration
                : duration;
        var now = DateTimeOffset.UtcNow;
        CapsuleEvent? replacedEvent = null;
        CapsuleEvent countdownEvent;
        CountdownTimerStatus status;

        lock (_gate)
        {
            if (_eventId is not null)
            {
                replacedEvent = CreateTerminalEvent(
                    CapsuleTaskState.Cancelled,
                    "已由新计时器替换",
                    now,
                    TimeSpan.FromSeconds(2));
            }

            _eventId = $"timer:{Guid.NewGuid():N}";
            _title = string.IsNullOrWhiteSpace(title)
                ? FormatDefaultTitle(duration)
                : title.Trim();
            _duration = duration;
            _pausedRemaining = duration;
            _deadline = now + duration;
            _createdAt = now;
            _lastPublishedSecond = -1;
            _isPaused = false;
            countdownEvent = CreateRunningEvent(now, duration);
            status = CreateStatus(now);
            _lastPublishedSecond = GetDisplaySeconds(duration);
            _timer.Change(TickInterval, TickInterval);
        }

        if (replacedEvent is not null)
        {
            EventChanged?.Invoke(replacedEvent);
        }

        EventChanged?.Invoke(countdownEvent);
        StatusChanged?.Invoke(status);
    }

    internal void TogglePause()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        CapsuleEvent? countdownEvent = null;
        CountdownTimerStatus? status = null;

        lock (_gate)
        {
            if (_eventId is null)
            {
                return;
            }

            if (_isPaused)
            {
                _deadline = now + _pausedRemaining;
                _isPaused = false;
            }
            else
            {
                _pausedRemaining = GetRemaining(now);
                _isPaused = true;
            }

            _lastPublishedSecond = GetDisplaySeconds(_pausedRemaining);
            countdownEvent = CreateRunningEvent(now, _pausedRemaining);
            status = CreateStatus(now);
        }

        EventChanged?.Invoke(countdownEvent);
        StatusChanged?.Invoke(status);
    }

    internal void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        CapsuleEvent? cancelledEvent = null;
        lock (_gate)
        {
            if (_eventId is null)
            {
                return;
            }

            cancelledEvent = CreateTerminalEvent(
                CapsuleTaskState.Cancelled,
                "计时已取消",
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(4));
            ResetActiveTimer();
        }

        EventChanged?.Invoke(cancelledEvent);
        StatusChanged?.Invoke(CountdownTimerStatus.Inactive);
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
            ResetActiveTimer();
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
        CapsuleEvent? countdownEvent = null;
        CountdownTimerStatus? status = null;

        lock (_gate)
        {
            if (_eventId is null || _isPaused)
            {
                return;
            }

            var remaining = GetRemaining(now);
            if (remaining <= TimeSpan.Zero)
            {
                countdownEvent = CreateTerminalEvent(
                    CapsuleTaskState.Succeeded,
                    "计时结束",
                    now,
                    null);
                ResetActiveTimer();
                status = CountdownTimerStatus.Inactive;
            }
            else
            {
                var displaySecond = GetDisplaySeconds(remaining);
                if (displaySecond == _lastPublishedSecond)
                {
                    return;
                }

                _lastPublishedSecond = displaySecond;
                countdownEvent = CreateRunningEvent(now, remaining);
                status = CreateStatus(now);
            }
        }

        EventChanged?.Invoke(countdownEvent);
        StatusChanged?.Invoke(status);
    }

    private CapsuleEvent CreateRunningEvent(
        DateTimeOffset now,
        TimeSpan remaining)
    {
        var progress = _duration <= TimeSpan.Zero
            ? 0
            : Math.Clamp(
                1 - (remaining.TotalMilliseconds / _duration.TotalMilliseconds),
                0,
                1);
        var displayRemaining = FormatRemaining(remaining);
        var message = _isPaused
            ? $"已暂停 · 剩余 {displayRemaining}"
            : $"剩余 {displayRemaining}";

        return new CapsuleEvent(
            _eventId!,
            CapsuleEventKind.Timer,
            CapsuleEventPriority.Normal,
            "timer",
            "计时器",
            _isPaused ? $"{_title}（已暂停）" : _title,
            message,
            progress,
            CapsuleTaskState.Running,
            EventPrivacyLevel.Full,
            _createdAt,
            null);
    }

    private CapsuleEvent CreateTerminalEvent(
        CapsuleTaskState state,
        string message,
        DateTimeOffset now,
        TimeSpan? visibleDuration)
    {
        return new CapsuleEvent(
            _eventId!,
            CapsuleEventKind.Timer,
            state == CapsuleTaskState.Succeeded
                ? CapsuleEventPriority.High
                : CapsuleEventPriority.Normal,
            "timer",
            "计时器",
            _title,
            message,
            state == CapsuleTaskState.Succeeded ? 1 : null,
            state,
            EventPrivacyLevel.Full,
            _createdAt,
            visibleDuration is { } duration
                ? now + duration
                : null);
    }

    private CountdownTimerStatus CreateStatus(DateTimeOffset now)
    {
        if (_eventId is null)
        {
            return CountdownTimerStatus.Inactive;
        }

        return new CountdownTimerStatus(
            true,
            _isPaused,
            _eventId,
            _title,
            _isPaused ? _pausedRemaining : GetRemaining(now));
    }

    private TimeSpan GetRemaining(DateTimeOffset now)
    {
        var remaining = _isPaused
            ? _pausedRemaining
            : _deadline - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ResetActiveTimer()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eventId = null;
        _title = string.Empty;
        _duration = TimeSpan.Zero;
        _pausedRemaining = TimeSpan.Zero;
        _deadline = default;
        _createdAt = default;
        _lastPublishedSecond = -1;
        _isPaused = false;
    }

    private static long GetDisplaySeconds(TimeSpan remaining)
    {
        return Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
    }

    private static string FormatDefaultTitle(TimeSpan duration)
    {
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        return $"{minutes} 分钟倒计时";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var totalSeconds = GetDisplaySeconds(remaining);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }
}
