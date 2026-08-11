using DynamicCapsule.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DynamicCapsule.Services;

internal sealed class WindowsClockTimerSyncService : IDisposable
{
    internal const string SourceId = "windows-clock";
    internal const string ClockAppUserModelId =
        "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";
    internal const string ClockShellTarget =
        $"shell:AppsFolder\\{ClockAppUserModelId}";

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, TrackedTimer> _timers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedStopwatch> _stopwatches =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ControlRequest> _controlRequests = new();
    private readonly System.Threading.Timer _pollTimer;

    private AutomationElement? _clockRoot;
    private int _pollInProgress;
    private bool _started;
    private bool _disposed;
    private string _status = BuildStatus(
        clockWindowAvailable: false,
        timerPageAvailable: false,
        timerCount: 0,
        stopwatchPageAvailable: false,
        stopwatchCount: 0);

    internal WindowsClockTimerSyncService()
    {
        _pollTimer = new System.Threading.Timer(
            _ => Poll(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal event Action<IReadOnlyList<CapsuleEvent>, IReadOnlyList<string>>?
        EventsChanged;
    internal event Action<string>? StatusChanged;

    internal string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _pollTimer.Change(TimeSpan.Zero, PollInterval);
    }

    internal bool TryGetControlState(
        string eventId,
        out WindowsClockTimerControlState state)
    {
        lock (_gate)
        {
            if (_timers.TryGetValue(eventId, out var timer))
            {
                state = new WindowsClockTimerControlState(
                    timer.IsPaused,
                    timer.PlayPauseButton is not null
                    && timer.ResetButton is not null);
                return true;
            }

            if (_stopwatches.TryGetValue(eventId, out var stopwatch))
            {
                state = new WindowsClockTimerControlState(
                    stopwatch.IsPaused,
                    stopwatch.PlayPauseButton is not null
                    && stopwatch.ResetButton is not null);
                return true;
            }
        }

        state = new WindowsClockTimerControlState(false, false);
        return false;
    }

    internal Task<bool> TogglePauseAsync(string eventId)
    {
        return QueueControlRequest(eventId, reset: false);
    }

    internal Task<bool> ResetAsync(string eventId)
    {
        return QueueControlRequest(eventId, reset: true);
    }

    internal Task<bool> OpenTimerAsync()
    {
        return OpenClockPageAsync(
            ["TimerButton", "TimerNavigationViewItem"],
            ["Timer", "计时器"]);
    }

    internal Task<bool> OpenStopwatchAsync()
    {
        return OpenClockPageAsync(
            [
                "StopwatchButton",
                "StopWatchButton",
                "StopwatchNavigationViewItem"
            ],
            ["Stopwatch", "秒表"]);
    }

    private async Task<bool> OpenClockPageAsync(
        IReadOnlyList<string> automationIds,
        IReadOnlyList<string> names)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryLaunchClock())
        {
            return false;
        }

        return await Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 50 && !_disposed; attempt++)
            {
                await Task.Delay(160).ConfigureAwait(false);
                InvalidateClockRoot();
                var clockRoot = GetClockRoot();
                var pageButton = FindFirstByAutomationIds(
                    clockRoot,
                    [.. automationIds])
                    ?? FindButtonByName(
                        clockRoot,
                        [.. names]);
                if (pageButton is null)
                {
                    continue;
                }

                if (TryInvoke(pageButton))
                {
                    _pollTimer.Change(TimeSpan.Zero, PollInterval);
                    return true;
                }
            }

            return false;
        }).ConfigureAwait(false);
    }

    private static bool TryLaunchClock()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = ClockShellTarget,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-clock:",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer.Dispose();
        lock (_gate)
        {
            _timers.Clear();
            _stopwatches.Clear();
            while (_controlRequests.TryDequeue(out var request))
            {
                request.Completion.TrySetResult(false);
            }
            _clockRoot = null;
        }
    }

    private Task<bool> QueueControlRequest(
        string eventId,
        bool reset)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_timers.ContainsKey(eventId)
                && !_stopwatches.ContainsKey(eventId))
            {
                completion.SetResult(false);
                return completion.Task;
            }

            _controlRequests.Enqueue(new ControlRequest(
                eventId,
                reset,
                completion));
        }

        if (!_disposed)
        {
            _pollTimer.Change(TimeSpan.Zero, PollInterval);
        }

        return completion.Task;
    }

    private void Poll()
    {
        if (_disposed
            || Interlocked.Exchange(ref _pollInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var observation = ObserveClock();
            ProcessControlRequests(observation);
            ApplyObservation(observation, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            InvalidateClockRoot();
            ApplyObservation(
                new TimerObservation(false, false, []),
                DateTimeOffset.UtcNow);
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
        }
    }

    private void ProcessControlRequests(TimerObservation observation)
    {
        ControlRequest[] requests;
        lock (_gate)
        {
            requests = _controlRequests.ToArray();
            _controlRequests.Clear();
        }

        foreach (var request in requests)
        {
            var timer = observation.Timers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.EventId,
                    request.EventId,
                    StringComparison.OrdinalIgnoreCase));
            if (timer is null)
            {
                var stopwatch = observation.Stopwatch;
                if (stopwatch is null
                    || !string.Equals(
                        stopwatch.EventId,
                        request.EventId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    request.Completion.TrySetResult(false);
                    continue;
                }

                ProcessStopwatchControlRequest(request, stopwatch);
                continue;
            }

            ProcessTimerControlRequest(request, timer);
        }
    }

    private void ProcessTimerControlRequest(
        ControlRequest request,
        ObservedTimer timer)
    {
        if (!request.Reset)
        {
            request.Completion.TrySetResult(
                timer.PlayPauseButton is not null
                && TryInvoke(timer.PlayPauseButton));
            return;
        }

        if (timer.IsPaused)
        {
            request.Completion.TrySetResult(
                timer.ResetButton is not null
                && TryInvoke(timer.ResetButton));
            return;
        }

        if (!request.PauseRequested)
        {
            if (timer.PlayPauseButton is null
                || !TryInvoke(timer.PlayPauseButton))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            RequeueControlRequest(request with
            {
                PauseRequested = true,
                Attempts = request.Attempts + 1
            });
            return;
        }

        if (request.Attempts >= 6)
        {
            request.Completion.TrySetResult(false);
            return;
        }

        RequeueControlRequest(request with
        {
            Attempts = request.Attempts + 1
        });
    }

    private void ProcessStopwatchControlRequest(
        ControlRequest request,
        ObservedStopwatch stopwatch)
    {
        if (!request.Reset)
        {
            request.Completion.TrySetResult(
                stopwatch.PlayPauseButton is not null
                && TryInvoke(stopwatch.PlayPauseButton));
            return;
        }

        if (stopwatch.IsPaused)
        {
            request.Completion.TrySetResult(
                stopwatch.ResetButton is not null
                && TryInvoke(stopwatch.ResetButton));
            return;
        }

        if (!request.PauseRequested)
        {
            if (stopwatch.PlayPauseButton is null
                || !TryInvoke(stopwatch.PlayPauseButton))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            RequeueControlRequest(request with
            {
                PauseRequested = true,
                Attempts = request.Attempts + 1
            });
            return;
        }

        if (request.Attempts >= 6)
        {
            request.Completion.TrySetResult(false);
            return;
        }

        RequeueControlRequest(request with
        {
            Attempts = request.Attempts + 1
        });
    }

    private void RequeueControlRequest(ControlRequest request)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                request.Completion.TrySetResult(false);
                return;
            }

            _controlRequests.Enqueue(request);
        }
    }

    private TimerObservation ObserveClock()
    {
        var clockRoot = GetClockRoot();
        if (clockRoot is null)
        {
            return new TimerObservation(false, false, []);
        }

        var timerObservation = ObserveTimers(clockRoot);
        var stopwatch = ObserveStopwatch(
            clockRoot,
            out var stopwatchPageAvailable);
        return timerObservation with
        {
            StopwatchPageAvailable = stopwatchPageAvailable,
            Stopwatch = stopwatch
        };
    }

    private static TimerObservation ObserveTimers(
        AutomationElement clockRoot)
    {
        var timerPage = FindByAutomationId(
            clockRoot,
            "TimerScrollViewer");
        if (timerPage is null)
        {
            return new TimerObservation(true, false, []);
        }

        var cards = timerPage.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                "TimerViewGrid"));
        var observedTimers = new List<ObservedTimer>();
        var titleOrdinals = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);

        foreach (AutomationElement card in cards)
        {
            var remainingElement = FindByAutomationId(
                card,
                "TimerValueText");
            var titleElement = FindByAutomationId(
                card,
                "TimerNameText");
            var playPauseButton = FindByAutomationId(
                card,
                "TimerPlayPauseButton");
            var resetButton = FindByAutomationId(
                card,
                "TimerResetButton");

            var remainingText = GetName(remainingElement);
            var title = GetName(titleElement);
            var cardName = GetName(card);
            var actionName = GetName(playPauseButton);
            var runState = WindowsClockTimerParser.ParseRunState(
                cardName,
                actionName);
            if (runState is WindowsClockTimerRunState.NotStarted
                or WindowsClockTimerRunState.Unknown
                || !WindowsClockTimerParser.TryParseDuration(
                    remainingText,
                    out var remaining))
            {
                continue;
            }

            title = string.IsNullOrWhiteSpace(title)
                ? "Windows 计时器"
                : title.Trim();
            titleOrdinals.TryGetValue(title, out var ordinal);
            titleOrdinals[title] = ordinal + 1;
            observedTimers.Add(new ObservedTimer(
                WindowsClockTimerParser.CreateEventId(title, ordinal),
                title,
                remaining,
                runState == WindowsClockTimerRunState.Paused,
                playPauseButton,
                resetButton));
        }

        return new TimerObservation(true, true, observedTimers);
    }

    private static ObservedStopwatch? ObserveStopwatch(
        AutomationElement clockRoot,
        out bool stopwatchPageAvailable)
    {
        stopwatchPageAvailable = false;
        var stopwatchPage = FindFirstByAutomationIds(
            clockRoot,
            "StopwatchScrollViewer",
            "StopwatchPage",
            "StopWatchPage",
            "StopwatchView",
            "StopwatchPivotItem");
        var stopwatchNavigationItem = FindFirstByAutomationIds(
            clockRoot,
            "StopwatchButton",
            "StopWatchButton",
            "StopwatchNavigationViewItem")
            ?? FindButtonByName(
                clockRoot,
                "Stopwatch",
                "秒表");
        var stopwatchPageIsVisible = stopwatchPage is not null
                                     && IsElementVisible(stopwatchPage);
        var stopwatchNavigationIsSelected = IsSelectionItemSelected(
            stopwatchNavigationItem);
        var visibleElapsedElement = FindFirstByAutomationIds(
            clockRoot,
            "StopwatchValueText",
            "StopWatchValueText",
            "StopwatchTimerText",
            "ElapsedTimeText",
            "TimeElapsedText");
        var stopwatchControlsAreVisible = visibleElapsedElement is not null
                                          && IsElementVisible(
                                              visibleElapsedElement);
        if (!stopwatchPageIsVisible
            && !stopwatchNavigationIsSelected
            && !stopwatchControlsAreVisible)
        {
            return null;
        }

        stopwatchPageAvailable = true;
        var searchRoot = stopwatchPageIsVisible
            ? stopwatchPage!
            : clockRoot;
        var elapsedElement = stopwatchControlsAreVisible
            ? visibleElapsedElement
            : FindFirstByAutomationIds(
                searchRoot,
                "StopwatchValueText",
                "StopWatchValueText",
                "StopwatchTimerText",
                "ElapsedTimeText",
                "TimeElapsedText");
        var elapsedText = GetName(elapsedElement);
        if (!TryParseStopwatchElapsed(elapsedText, out var elapsed))
        {
            elapsedElement = FindDurationTextElement(searchRoot);
            elapsedText = GetName(elapsedElement);
            if (!TryParseStopwatchElapsed(elapsedText, out elapsed))
            {
                return null;
            }
        }

        var playPauseButton = FindFirstByAutomationIds(
            searchRoot,
            "StopwatchPlayPauseButton",
            "StopWatchPlayPauseButton",
            "StopwatchStartPauseButton",
            "StartStopButton",
            "PlayPauseButton")
            ?? FindButtonByName(
                searchRoot,
                "Pause",
                "Start",
                "Resume",
                "Continue",
                "暂停",
                "开始",
                "继续");
        var resetButton = FindFirstByAutomationIds(
            searchRoot,
            "StopwatchResetButton",
            "StopWatchResetButton",
            "ResetButton")
            ?? FindButtonByName(
                searchRoot,
                "Reset",
                "重置",
                "清除");
        var actionName = GetName(playPauseButton);
        var isPaused = ContainsAny(
            actionName,
            "Start",
            "Resume",
            "Continue",
            "开始",
            "继续");

        if (elapsed <= TimeSpan.Zero && isPaused)
        {
            return null;
        }

        return new ObservedStopwatch(
            "windows-clock:stopwatch",
            elapsed,
            isPaused,
            playPauseButton,
            resetButton);
    }

    private void ApplyObservation(
        TimerObservation observation,
        DateTimeOffset now)
    {
        var changedEvents = new List<CapsuleEvent>();
        var removedIds = new List<string>();
        string? changedStatus = null;

        lock (_gate)
        {
            var observedIds = new HashSet<string>(
                observation.Timers.Select(timer => timer.EventId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var observed in observation.Timers)
            {
                if (!_timers.TryGetValue(
                        observed.EventId,
                        out var tracked))
                {
                    tracked = new TrackedTimer(
                        observed.EventId,
                        observed.Title,
                        observed.Remaining,
                        observed.IsPaused,
                        now);
                    _timers[observed.EventId] = tracked;
                }

                var stateChanged = tracked.IsPaused != observed.IsPaused;
                tracked.Title = observed.Title;
                tracked.IsPaused = observed.IsPaused;
                tracked.PausedRemaining = observed.Remaining;
                tracked.Deadline = now + observed.Remaining;
                tracked.PlayPauseButton = observed.PlayPauseButton;
                tracked.ResetButton = observed.ResetButton;
                tracked.LastObservedAt = now;

                if (WindowsClockTimerParser.TryParseDuration(
                        observed.Title,
                        out var titleDuration)
                    && titleDuration >= observed.Remaining)
                {
                    tracked.TotalDuration = titleDuration;
                }
                else if (tracked.TotalDuration < observed.Remaining)
                {
                    tracked.TotalDuration = observed.Remaining;
                }

                if (observed.Remaining <= TimeSpan.Zero)
                {
                    tracked.PausedRemaining = TimeSpan.Zero;
                    tracked.Deadline = now;
                    if (!tracked.IsCompleted)
                    {
                        tracked.IsCompleted = true;
                        tracked.LastPublishedSecond = 0;
                        changedEvents.Add(CreateCompletedEvent(
                            tracked));
                    }

                    tracked.WasObserved = true;
                    continue;
                }

                tracked.IsCompleted = false;

                var displaySecond = GetDisplaySecond(
                    observed.Remaining);
                if (tracked.LastPublishedSecond != displaySecond
                    || stateChanged
                    || !tracked.WasObserved)
                {
                    tracked.LastPublishedSecond = displaySecond;
                    changedEvents.Add(CreateRunningEvent(
                        tracked,
                        observed.Remaining));
                }

                tracked.WasObserved = true;
            }

            if (observation.TimerPageAvailable)
            {
                foreach (var missingId in _timers.Keys
                             .Where(id => !observedIds.Contains(id))
                             .ToArray())
                {
                    _timers.Remove(missingId);
                    removedIds.Add(missingId);
                }
            }
            else
            {
                foreach (var tracked in _timers.Values.ToArray())
                {
                    tracked.WasObserved = false;
                    tracked.PlayPauseButton = null;
                    tracked.ResetButton = null;
                    if (tracked.IsCompleted)
                    {
                        continue;
                    }

                    var remaining = tracked.IsPaused
                        ? tracked.PausedRemaining
                        : tracked.Deadline - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        tracked.IsCompleted = true;
                        tracked.PausedRemaining = TimeSpan.Zero;
                        tracked.Deadline = now;
                        changedEvents.Add(CreateCompletedEvent(
                            tracked));
                        continue;
                    }

                    var displaySecond = GetDisplaySecond(remaining);
                    if (tracked.LastPublishedSecond == displaySecond)
                    {
                        continue;
                    }

                    tracked.LastPublishedSecond = displaySecond;
                    changedEvents.Add(CreateRunningEvent(
                        tracked,
                        remaining));
                }
            }

            var observedStopwatch = observation.Stopwatch;
            if (observedStopwatch is not null)
            {
                if (!_stopwatches.TryGetValue(
                        observedStopwatch.EventId,
                        out var trackedStopwatch))
                {
                    trackedStopwatch = new TrackedStopwatch(
                        observedStopwatch.EventId,
                        now - observedStopwatch.Elapsed);
                    _stopwatches[observedStopwatch.EventId] =
                        trackedStopwatch;
                }

                var stateChanged = trackedStopwatch.IsPaused
                                   != observedStopwatch.IsPaused;
                trackedStopwatch.Elapsed = observedStopwatch.Elapsed;
                trackedStopwatch.IsPaused = observedStopwatch.IsPaused;
                trackedStopwatch.PlayPauseButton =
                    observedStopwatch.PlayPauseButton;
                trackedStopwatch.ResetButton = observedStopwatch.ResetButton;
                trackedStopwatch.LastObservedAt = now;
                trackedStopwatch.WasObserved = true;

                var displaySecond = GetDisplaySecond(
                    observedStopwatch.Elapsed);
                if (trackedStopwatch.LastPublishedSecond != displaySecond
                    || stateChanged)
                {
                    trackedStopwatch.LastPublishedSecond = displaySecond;
                    changedEvents.Add(CreateStopwatchEvent(
                        trackedStopwatch));
                }
            }
            else if (observation.StopwatchPageAvailable)
            {
                foreach (var missingId in _stopwatches.Keys.ToArray())
                {
                    _stopwatches.Remove(missingId);
                    removedIds.Add(missingId);
                }
            }
            else
            {
                foreach (var trackedStopwatch in _stopwatches.Values.ToArray())
                {
                    trackedStopwatch.WasObserved = false;
                    trackedStopwatch.PlayPauseButton = null;
                    trackedStopwatch.ResetButton = null;
                    if (!trackedStopwatch.IsPaused)
                    {
                        trackedStopwatch.Elapsed += now
                                                     - trackedStopwatch
                                                         .LastObservedAt;
                        trackedStopwatch.LastObservedAt = now;
                    }

                    var displaySecond = GetDisplaySecond(
                        trackedStopwatch.Elapsed);
                    if (trackedStopwatch.LastPublishedSecond == displaySecond)
                    {
                        continue;
                    }

                    trackedStopwatch.LastPublishedSecond = displaySecond;
                    changedEvents.Add(CreateStopwatchEvent(
                        trackedStopwatch));
                }
            }

            var nextStatus = BuildStatus(
                observation.ClockWindowAvailable,
                observation.TimerPageAvailable,
                _timers.Count,
                observation.StopwatchPageAvailable,
                _stopwatches.Count);

            if (!string.Equals(
                    _status,
                    nextStatus,
                    StringComparison.Ordinal))
            {
                _status = nextStatus;
                changedStatus = nextStatus;
            }
        }

        if (changedEvents.Count > 0 || removedIds.Count > 0)
        {
            EventsChanged?.Invoke(changedEvents, removedIds);
        }

        if (changedStatus is not null)
        {
            StatusChanged?.Invoke(changedStatus);
        }
    }

    internal static string BuildStatus(
        bool clockWindowAvailable,
        bool timerPageAvailable,
        int timerCount,
        bool stopwatchPageAvailable,
        int stopwatchCount)
    {
        timerCount = Math.Max(0, timerCount);
        stopwatchCount = Math.Max(0, stopwatchCount);
        if (!timerPageAvailable && !stopwatchPageAvailable)
        {
            if (timerCount == 0 && stopwatchCount == 0)
            {
                return clockWindowAvailable
                    ? "已检测到 Windows 时钟 · 计时器/秒表页面暂不可读取"
                    : "等待 Windows 时钟（打开“时钟 > 计时器或秒表”后同步）";
            }

            var mirroredActivities = new List<string>(2);
            if (timerCount > 0)
            {
                mirroredActivities.Add($"{timerCount} 个计时器");
            }

            if (stopwatchCount > 0)
            {
                mirroredActivities.Add("秒表");
            }

            return (clockWindowAvailable
                       ? "Windows 时钟页面暂不可读取 · 本地镜像 "
                       : "Windows 时钟不可见 · 本地镜像 ")
                   + string.Join("、", mirroredActivities);
        }

        var synchronizedActivities = new List<string>(2);
        if (timerCount > 0)
        {
            synchronizedActivities.Add(timerPageAvailable
                ? $"同步 {timerCount} 个计时器"
                : $"本地镜像 {timerCount} 个计时器");
        }

        if (stopwatchCount > 0)
        {
            synchronizedActivities.Add(stopwatchPageAvailable
                ? "同步秒表"
                : "本地镜像秒表");
        }

        return synchronizedActivities.Count == 0
            ? "已连接 Windows 时钟 · 暂无运行中的计时器或秒表"
            : "已连接 Windows 时钟 · "
              + string.Join(" · ", synchronizedActivities);
    }

    private AutomationElement? GetClockRoot()
    {
        AutomationElement? cached;
        lock (_gate)
        {
            cached = _clockRoot;
        }

        if (cached is not null && IsElementAvailable(cached))
        {
            return cached;
        }

        var desktopChildren = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            Condition.TrueCondition);
        foreach (AutomationElement candidate in desktopChildren)
        {
            if (!IsClockCandidate(candidate))
            {
                continue;
            }

            lock (_gate)
            {
                _clockRoot = candidate;
            }

            return candidate;
        }

        InvalidateClockRoot();
        return null;
    }

    private static bool IsClockCandidate(AutomationElement candidate)
    {
        var title = GetName(candidate);
        if (title.Equals("时钟", StringComparison.OrdinalIgnoreCase)
            || title.Equals("Clock", StringComparison.OrdinalIgnoreCase)
            || title.Equals("Alarms & Clock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return FindByAutomationId(candidate, "TimerButton") is not null
               || FindByAutomationId(candidate, "StopwatchButton") is not null;
    }

    private void InvalidateClockRoot()
    {
        lock (_gate)
        {
            _clockRoot = null;
        }
    }

    private static AutomationElement? FindByAutomationId(
        AutomationElement? root,
        string automationId)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            return root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static AutomationElement? FindFirstByAutomationIds(
        AutomationElement? root,
        params string[] automationIds)
    {
        foreach (var automationId in automationIds)
        {
            var element = FindByAutomationId(root, automationId);
            if (element is not null)
            {
                return element;
            }
        }

        return null;
    }

    private static AutomationElement? FindDescendantByName(
        AutomationElement? root,
        params string[] nameParts)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            var descendants = root.FindAll(
                TreeScope.Descendants,
                Condition.TrueCondition);
            foreach (AutomationElement descendant in descendants)
            {
                if (ContainsAny(GetName(descendant), nameParts))
                {
                    return descendant;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }

    private static AutomationElement? FindButtonByName(
        AutomationElement? root,
        params string[] nameParts)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                if (ContainsAny(GetName(button), nameParts))
                {
                    return button;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }

    private static AutomationElement? FindDurationTextElement(
        AutomationElement? root)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            var texts = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Text));
            AutomationElement? best = null;
            var bestDuration = TimeSpan.Zero;
            foreach (AutomationElement text in texts)
            {
                if (!TryParseStopwatchElapsed(
                        GetName(text),
                        out var duration))
                {
                    continue;
                }

                if (duration >= bestDuration)
                {
                    best = text;
                    bestDuration = duration;
                }
            }

            return best;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static bool TryParseStopwatchElapsed(
        string? text,
        out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        if (!WindowsClockTimerParser.TryParseDuration(text, out var parsed))
        {
            return false;
        }

        elapsed = parsed;
        return true;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(
            candidate,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string GetName(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static bool IsElementAvailable(AutomationElement element)
    {
        try
        {
            _ = element.Current.ProcessId;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsElementVisible(AutomationElement element)
    {
        try
        {
            return !element.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsSelectionItemSelected(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.TryGetCurrentPattern(
                       SelectionItemPattern.Pattern,
                       out var pattern)
                   && ((SelectionItemPattern)pattern).Current.IsSelected;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
            or InvalidOperationException
            or COMException)
        {
            return false;
        }
    }

    private static bool TryInvoke(AutomationElement button)
    {
        try
        {
            if (!button.TryGetCurrentPattern(
                    InvokePattern.Pattern,
                    out var pattern))
            {
                if (button.TryGetCurrentPattern(
                        SelectionItemPattern.Pattern,
                        out var selectionItemPattern))
                {
                    ((SelectionItemPattern)selectionItemPattern).Select();
                    return true;
                }

                if (button.TryGetCurrentPattern(
                        TogglePattern.Pattern,
                        out var togglePattern))
                {
                    ((TogglePattern)togglePattern).Toggle();
                    return true;
                }

                return false;
            }

            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
            or InvalidOperationException
            or COMException)
        {
            return false;
        }
    }

    private static CapsuleEvent CreateRunningEvent(
        TrackedTimer timer,
        TimeSpan remaining)
    {
        double? progress = timer.TotalDuration > TimeSpan.Zero
            ? Math.Clamp(
                1 - remaining.TotalMilliseconds
                / timer.TotalDuration.TotalMilliseconds,
                0,
                1)
            : null;
        var remainingText = WindowsClockTimerParser.FormatRemaining(
            remaining);
        return new CapsuleEvent(
            timer.EventId,
            CapsuleEventKind.Timer,
            CapsuleEventPriority.Normal,
            SourceId,
            "Windows 时钟",
            timer.Title,
            timer.IsPaused
                ? $"已暂停 · 剩余 {remainingText}"
                : $"剩余 {remainingText}",
            progress,
            CapsuleTaskState.Running,
            EventPrivacyLevel.Full,
            timer.CreatedAt,
            null);
    }

    private static CapsuleEvent CreateCompletedEvent(
        TrackedTimer timer)
    {
        return new CapsuleEvent(
            timer.EventId,
            CapsuleEventKind.Timer,
            CapsuleEventPriority.High,
            SourceId,
            "Windows 时钟",
            timer.Title,
            "计时结束",
            1,
            CapsuleTaskState.Succeeded,
            EventPrivacyLevel.Full,
            timer.CreatedAt,
            null);
    }

    private static CapsuleEvent CreateStopwatchEvent(
        TrackedStopwatch stopwatch)
    {
        var elapsedText = WindowsClockTimerParser.FormatRemaining(
            stopwatch.Elapsed);
        return new CapsuleEvent(
            stopwatch.EventId,
            CapsuleEventKind.Stopwatch,
            CapsuleEventPriority.Normal,
            SourceId,
            "Windows 时钟",
            stopwatch.IsPaused ? "秒表（已暂停）" : "秒表",
            stopwatch.IsPaused
                ? $"已暂停 · {elapsedText}"
                : $"已计时 {elapsedText}",
            (stopwatch.Elapsed.TotalSeconds % 60) / 60,
            CapsuleTaskState.Running,
            EventPrivacyLevel.Full,
            stopwatch.CreatedAt,
            null);
    }

    private static long GetDisplaySecond(TimeSpan remaining)
    {
        return Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
    }

    private sealed class TrackedTimer(
        string eventId,
        string title,
        TimeSpan remaining,
        bool isPaused,
        DateTimeOffset createdAt)
    {
        internal string EventId { get; } = eventId;
        internal string Title { get; set; } = title;
        internal TimeSpan PausedRemaining { get; set; } = remaining;
        internal TimeSpan TotalDuration { get; set; } = remaining;
        internal DateTimeOffset Deadline { get; set; } = createdAt + remaining;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset LastObservedAt { get; set; } = createdAt;
        internal long LastPublishedSecond { get; set; } = -1;
        internal bool IsPaused { get; set; } = isPaused;
        internal bool IsCompleted { get; set; }
        internal bool WasObserved { get; set; }
        internal AutomationElement? PlayPauseButton { get; set; }
        internal AutomationElement? ResetButton { get; set; }
    }

    private sealed class TrackedStopwatch(
        string eventId,
        DateTimeOffset createdAt)
    {
        internal string EventId { get; } = eventId;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset LastObservedAt { get; set; } = createdAt;
        internal TimeSpan Elapsed { get; set; }
        internal long LastPublishedSecond { get; set; } = -1;
        internal bool IsPaused { get; set; }
        internal bool WasObserved { get; set; }
        internal AutomationElement? PlayPauseButton { get; set; }
        internal AutomationElement? ResetButton { get; set; }
    }

    private sealed record ObservedTimer(
        string EventId,
        string Title,
        TimeSpan Remaining,
        bool IsPaused,
        AutomationElement? PlayPauseButton,
        AutomationElement? ResetButton);

    private sealed record ObservedStopwatch(
        string EventId,
        TimeSpan Elapsed,
        bool IsPaused,
        AutomationElement? PlayPauseButton,
        AutomationElement? ResetButton);

    private sealed record TimerObservation(
        bool ClockWindowAvailable,
        bool TimerPageAvailable,
        IReadOnlyList<ObservedTimer> Timers,
        bool StopwatchPageAvailable = false,
        ObservedStopwatch? Stopwatch = null);

    private sealed record ControlRequest(
        string EventId,
        bool Reset,
        TaskCompletionSource<bool> Completion,
        bool PauseRequested = false,
        int Attempts = 0);
}
