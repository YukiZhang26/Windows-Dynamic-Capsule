using DynamicCapsule.Models;
using DynamicCapsule.Services;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace DynamicCapsule;

public partial class CapsuleWindow : Window
{
    private const double CompactWidth = 140;
    private const double CompactHeight = 36;
    private const double PowerOffWidth = 112;
    private const double MediaCompactWidth = 340;
    private const double MediaCompactHeight = 52;
    private const double EventAlertWidth = 380;
    private const double PrivateEventAlertWidth = 190;
    private const double DualTaskWidth = 500;
    private const double SideArrowLaneWidth = 30;
    private const double EventAlertHeight = 76;
    private const double EventCompactWidth = 132;
    private const double DualEventCompactWidth = 176;
    private const double MediaEventCompactWidth = 176;
    private const double EventCompactHeight = 42;
    private const double ExpandedWidth = 504;
    private const double HostWidth = DualTaskWidth + SideArrowLaneWidth;
    private const double ExpandedHeight = 120;
    private const double MediaExpandedWidth = 440;
    private const double MediaExpandedHeight = 238;
    private const double MaximumTopGap = 48;
    private const double HostHeight = MediaExpandedHeight + MaximumTopGap;
    private const double TopStashVisibleHeight = 6;
    private static readonly Thickness MediaExpandedPadding =
        new(24, 16, 24, 16);
    private static readonly TimeSpan ResizeDuration =
        TimeSpan.FromMilliseconds(270);

    private readonly MediaSessionService _mediaService = new();
    private readonly LyricsService _lyricsService = new();
    private readonly DispatcherTimer _lyricsTimer;
    private readonly DispatcherTimer _eventCollapseTimer;
    private readonly NotificationService _notificationService = new();
    private readonly EventPolicyEngine _eventPolicyEngine = new();
    private readonly CapsuleEventScheduler _eventScheduler = new();
    private readonly LocalTaskPipeService _localTaskPipeService = new();
    private readonly CountdownTimerService _countdownTimerService = new();
    private readonly StopwatchService _stopwatchService = new();
    private readonly WindowsClockTimerSyncService
        _windowsClockTimerService = new();
    private readonly ConnectivityEventService _connectivityEventService = new();
    private readonly BrowserDownloadProgressService
        _browserDownloadProgressService = new();

    private volatile AppSettings _settings;
    private AccessibilityPreferences _accessibilityPreferences;
    private FullscreenDetector? _fullscreenDetector;
    private HwndSource? _source;
    private nint _windowHandle;
    private nint _targetMonitor;
    private MediaSnapshot? _mediaSnapshot;
    private CapsuleEvent? _activeCapsuleEvent;
    private CapsuleEvent? _secondaryCapsuleEvent;
    private CancellationTokenSource? _lyricsLookupCancellation;
    private LyricsLookupResult? _lyricsResult;
    private string? _lyricsTrackIdentity;
    private TimeSpan _timelineAnchorPosition;
    private DateTimeOffset _timelineAnchorCapturedAt;
    private bool _timelineIsPlaying;
    private string _mediaStatus = "正在连接系统媒体会话";
    private string _notificationStatus = "正在检查通知能力";
    private string _localTaskStatus = "本地任务管道尚未启动";
    private bool _isFullscreenDetected;
    private bool _isTopStashed;
    private bool _isSuppressed;
    private bool _isPaused;
    private bool _isQuickMenuOpen;
    private bool _isMediaScrubbing;
    private double _mediaProgressDurationSeconds = 1;
    private double _mediaScrubPositionSeconds;
    private TimeSpan? _pendingMediaSeekPosition;
    private DateTimeOffset _pendingMediaSeekUntil;
    private string? _pendingMediaSeekTrackIdentity;
    private bool _hasAppliedArtwork;
    private byte[]? _appliedArtworkBytes;
    private bool _isExpanded;
    private bool _isEventExpanded;
    private bool _isMediaExpandedOverEvent;
    private bool _isAnimatingSize;
    private bool _isTopStashAnimating;
    private bool _suppressNextCapsuleMouseLeftButtonUp;
    private int _sizeAnimationVersion;
    private int _presentationAnimationVersion;
    private int _topStashAnimationVersion;
    private bool ShouldAnimate =>
        _settings.EnableAnimations
        && !_accessibilityPreferences.ReduceMotion;

    private bool IsCompactActiveEvent =>
        IsLongRunningEvent(_activeCapsuleEvent)
        && !_isEventExpanded
        && !_isMediaExpandedOverEvent;

    private bool IsMediaEventCompact =>
        IsCompactActiveEvent
        && _mediaSnapshot is not null;

    private bool UseMediaSideArrow => !_isQuickMenuOpen
                                      && !_isPaused
                                      && _mediaSnapshot is not null
                                      && _activeCapsuleEvent is null
                                      && !_isExpanded;

    private bool UseGenericSideArrow => !_isQuickMenuOpen
                                        && !_isPaused
                                        && !UseMediaSideArrow;

    private bool HasNotificationReveal => !_settings.DoNotDisturb
                                          && (_activeCapsuleEvent?.Kind
                                                  == CapsuleEventKind.Notification
                                              || _secondaryCapsuleEvent?.Kind
                                                  == CapsuleEventKind.Notification);

    private bool ShouldStashSurface => _isTopStashed
                                       && !_isQuickMenuOpen
                                       && !HasNotificationReveal;

    private double BaseDesiredWidth => _isQuickMenuOpen
        ? ExpandedWidth
        : _isPaused
        ? PowerOffWidth
        : _activeCapsuleEvent is not null
        ? _isMediaExpandedOverEvent
            ? MediaExpandedWidth
            : IsCompactActiveEvent
            ? IsMediaEventCompact
                ? MediaEventCompactWidth
                : _secondaryCapsuleEvent is not null
                ? DualEventCompactWidth
                : EventCompactWidth
            : _secondaryCapsuleEvent is not null
            ? DualTaskWidth
            : _activeCapsuleEvent.PrivacyLevel == EventPrivacyLevel.IconOnly
                ? PrivateEventAlertWidth
                : EventAlertWidth
        : _isExpanded
            ? MediaExpandedWidth
            : _mediaSnapshot is null
                ? CompactWidth
                : MediaCompactWidth;

    private double DesiredWidth => BaseDesiredWidth
                                   + (UseGenericSideArrow
                                       ? SideArrowLaneWidth
                                       : 0);

    private double DesiredHeight => _isQuickMenuOpen
        ? ExpandedHeight
        : _isPaused
        ? CompactHeight
        : _activeCapsuleEvent is not null
        ? _isMediaExpandedOverEvent
            ? MediaExpandedHeight
            : IsCompactActiveEvent
            ? EventCompactHeight
            : EventAlertHeight
        : _isExpanded
            ? MediaExpandedHeight
            : _mediaSnapshot is null
                ? CompactHeight
                : MediaCompactHeight;

    internal CapsuleWindow(
        AppSettings settings,
        AccessibilityPreferences accessibilityPreferences)
    {
        _settings = settings.Normalize();
        _isTopStashed = _settings.TopStashed;
        _accessibilityPreferences = accessibilityPreferences;
        InitializeComponent();

        _lyricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _lyricsTimer.Tick += OnLyricsTimerTick;

        _eventCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2400)
        };
        _eventCollapseTimer.Tick += OnEventCollapseTimerTick;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;

        _mediaService.SnapshotChanged += OnMediaSnapshotChanged;
        _mediaService.StatusChanged += OnMediaStatusChanged;
        _notificationService.NotificationReceived += OnNotificationReceived;
        _notificationService.NotificationRemoved += OnNotificationRemoved;
        _notificationService.StatusChanged += OnNotificationStatusChanged;
        _eventScheduler.PresentedEventsChanged +=
            OnPresentedCapsuleEventsChanged;
        _localTaskPipeService.EventReceived += OnLocalTaskEventReceived;
        _localTaskPipeService.StatusChanged += OnLocalTaskStatusChanged;
        _countdownTimerService.EventChanged += OnCountdownEventChanged;
        _countdownTimerService.StatusChanged += OnCountdownStatusChanged;
        _stopwatchService.EventChanged += OnStopwatchEventChanged;
        _stopwatchService.StatusChanged += OnStopwatchStatusChanged;
        _windowsClockTimerService.EventChanged +=
            OnWindowsClockTimerEventChanged;
        _windowsClockTimerService.EventRemoved +=
            OnWindowsClockTimerEventRemoved;
        _connectivityEventService.EventReceived +=
            OnConnectivityEventReceived;
        _browserDownloadProgressService.EventChanged +=
            OnBrowserDownloadEventChanged;
        _browserDownloadProgressService.EventRemoved +=
            OnBrowserDownloadEventRemoved;
        _eventScheduler.SetPaused(_settings.DoNotDisturb);
    }

    internal event Action? SettingsRequested;
    internal event Action<bool>? PowerStateChangeRequested;
    internal event Action<bool>? DoNotDisturbChangeRequested;
    internal event Action<bool>? TopStashChangeRequested;
    internal event Action? ExitRequested;
    internal event Action<int?>? CountdownRequested;
    internal event Action<CountdownTimerStatus>? CountdownStatusChanged;
    internal event Action<StopwatchStatus>? StopwatchStatusChanged;
    internal event Action<bool, bool>? ActiveEventStatusChanged;
    internal event Action<NotificationListenerStatus, string>?
        NotificationStatusChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowMessageHook);

        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle);
        var updatedStyle = extendedStyle.ToInt64()
                           | NativeMethods.WsExToolWindow
                           | NativeMethods.WsExNoActivate;

        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlExStyle,
            new nint(updatedStyle));

        _targetMonitor = ResolveInitialMonitor();
        _fullscreenDetector = new FullscreenDetector(_windowHandle);
        _fullscreenDetector.StateChanged += OnFullscreenStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        UpdateTopStashPresentation(animate: false, DesiredHeight);
        PositionOnTargetMonitor();
        ApplyRoundedWindowRegion();
        _fullscreenDetector?.Start();
        if (!_isSuppressed)
        {
            Opacity = 1;
        }

        _localTaskPipeService.Start();
        _windowsClockTimerService.Start();
        _connectivityEventService.Start();
        _browserDownloadProgressService.Start();
        await _mediaService.StartAsync();
        await _notificationService.StartAsync(requestAccess: true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopSurfaceResizeAnimation();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;

        if (_fullscreenDetector is not null)
        {
            _fullscreenDetector.StateChanged -= OnFullscreenStateChanged;
            _fullscreenDetector.Dispose();
            _fullscreenDetector = null;
        }

        _mediaService.SnapshotChanged -= OnMediaSnapshotChanged;
        _mediaService.StatusChanged -= OnMediaStatusChanged;
        _mediaService.Dispose();

        _lyricsTimer.Stop();
        _lyricsTimer.Tick -= OnLyricsTimerTick;
        _eventCollapseTimer.Stop();
        _eventCollapseTimer.Tick -= OnEventCollapseTimerTick;
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        _lyricsService.Dispose();

        _notificationService.NotificationReceived -= OnNotificationReceived;
        _notificationService.NotificationRemoved -= OnNotificationRemoved;
        _notificationService.StatusChanged -= OnNotificationStatusChanged;
        _notificationService.Dispose();

        _localTaskPipeService.EventReceived -= OnLocalTaskEventReceived;
        _localTaskPipeService.StatusChanged -= OnLocalTaskStatusChanged;
        _localTaskPipeService.Dispose();
        _countdownTimerService.EventChanged -= OnCountdownEventChanged;
        _countdownTimerService.StatusChanged -= OnCountdownStatusChanged;
        _countdownTimerService.Dispose();
        _stopwatchService.EventChanged -= OnStopwatchEventChanged;
        _stopwatchService.StatusChanged -= OnStopwatchStatusChanged;
        _stopwatchService.Dispose();
        _windowsClockTimerService.EventChanged -=
            OnWindowsClockTimerEventChanged;
        _windowsClockTimerService.EventRemoved -=
            OnWindowsClockTimerEventRemoved;
        _windowsClockTimerService.Dispose();
        _connectivityEventService.EventReceived -=
            OnConnectivityEventReceived;
        _connectivityEventService.Dispose();
        _browserDownloadProgressService.EventChanged -=
            OnBrowserDownloadEventChanged;
        _browserDownloadProgressService.EventRemoved -=
            OnBrowserDownloadEventRemoved;
        _browserDownloadProgressService.Dispose();
        _eventScheduler.PresentedEventsChanged -=
            OnPresentedCapsuleEventsChanged;
        _eventScheduler.Dispose();
    }

    private void OnFullscreenStateChanged(FullscreenSnapshot snapshot)
    {
        var canFollowForeground =
            _settings.MonitorTarget == MonitorTargetMode.FollowActiveWindow;
        var targetChanged = canFollowForeground
                            && snapshot.MonitorHandle != nint.Zero
                            && snapshot.MonitorHandle != _targetMonitor;
        if (targetChanged)
        {
            _targetMonitor = snapshot.MonitorHandle;
        }

        _isFullscreenDetected = snapshot.IsFullscreen
                                && snapshot.MonitorHandle == _targetMonitor;
        ApplyVisibilitySuppression();

        if (targetChanged)
        {
            PositionOnTargetMonitor();
            ApplyRoundedWindowRegion();
        }
    }

    internal void SetPaused(bool isPaused)
    {
        if (_isPaused == isPaused)
        {
            return;
        }

        _isPaused = isPaused;
        _eventScheduler.SetPaused(isPaused || _settings.DoNotDisturb);
        if (isPaused)
        {
            _isQuickMenuOpen = false;
            _isExpanded = false;
            _fullscreenDetector?.Stop();
        }
        else
        {
            _fullscreenDetector?.Start();
            _fullscreenDetector?.Refresh();
        }

        UpdateSurfacePresentation();
        UpdateCompactVisibility();
        CapsuleSurface.Padding = new Thickness(12, 0, 12, 0);
        ApplyVisibilitySuppression();
        AnimateWindowSize(DesiredWidth, DesiredHeight);
    }

    internal void ApplySettings(AppSettings settings)
    {
        var previouslyAnimated = ShouldAnimate;
        var previousSettings = _settings;
        _settings = settings.Normalize();
        var topStashChanged =
            previousSettings.TopStashed != _settings.TopStashed;
        _isTopStashed = _settings.TopStashed;
        UpdateQuickDoNotDisturbButton();
        _eventScheduler.ApplyPolicy(capsuleEvent =>
            _eventPolicyEngine.Evaluate(
                capsuleEvent,
                _settings).CapsuleEvent);
        _eventScheduler.SetPaused(_isPaused || _settings.DoNotDisturb);

        if (previousSettings.MonitorTarget != _settings.MonitorTarget)
        {
            _targetMonitor = ResolveInitialMonitor();
        }

        if (previouslyAnimated && !ShouldAnimate)
        {
            StopAnimationsAndApplyCurrentLayout();
        }
        else if (!previousSettings.EnableAnimations && ShouldAnimate)
        {
            EventAlertContent.Opacity = 1;
            ExpandedContent.Opacity = 1;
            ExpandedContentTranslate.Y = 0;
        }

        if (_activeCapsuleEvent is not null)
        {
            ApplyEventGlyph(_activeCapsuleEvent);
        }

        _fullscreenDetector?.Refresh();
        ApplyVisibilitySuppression();
        UpdateTopStashPresentation(
            animate: topStashChanged && ShouldAnimate,
            DesiredHeight);
        PositionOnTargetMonitor();
        ApplyRoundedWindowRegion();
    }

    internal void ApplyAccessibilityPreferences(
        AccessibilityPreferences preferences)
    {
        var previouslyAnimated = ShouldAnimate;
        _accessibilityPreferences = preferences;
        if (previouslyAnimated && !ShouldAnimate)
        {
            StopAnimationsAndApplyCurrentLayout();
        }

        if (_activeCapsuleEvent is not null)
        {
            ApplyEventGlyph(_activeCapsuleEvent);
        }

        if (_secondaryCapsuleEvent is not null)
        {
            ApplySecondaryEvent(_secondaryCapsuleEvent);
        }
    }

    internal bool ToggleExpandedFromAccessibility()
    {
        if (_activeCapsuleEvent is not null)
        {
            return false;
        }

        ToggleExpanded();
        return true;
    }

    internal void StartCountdown(TimeSpan duration, string? title = null)
    {
        _countdownTimerService.Start(duration, title);
    }

    internal void StartStopwatch()
    {
        _stopwatchService.Start();
    }

    internal void ToggleStopwatchPause()
    {
        _stopwatchService.TogglePause();
    }

    internal void StopStopwatch()
    {
        _stopwatchService.Stop();
    }

    internal bool ShowNotificationPreview(
        EventPrivacyLevel privacyLevel)
    {
        if (_windowHandle == nint.Zero || Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        var snapshot = new NotificationSnapshot(
            uint.MaxValue,
            "DynamicCapsule.NotificationPreview",
            "微信（预览）",
            "林小雨",
            "今晚七点一起吃饭吗？这是一条本地预览，不会读取或保存真实消息。",
            DateTimeOffset.Now);
        var previewSettings = (_settings with
        {
            PrivacyLevel = privacyLevel,
            NotificationAllowList = [],
            NotificationBlockList = []
        }).Normalize();
        var decision = _eventPolicyEngine.Evaluate(
            CapsuleEvent.FromNotification(snapshot),
            previewSettings);
        if (decision.CapsuleEvent is not { } previewEvent)
        {
            return false;
        }

        _eventScheduler.Publish(previewEvent with
        {
            EventId = $"notification-preview:{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(6)
        });
        return true;
    }

    internal void ToggleCountdownPause()
    {
        var activeEvent = _eventScheduler.ActiveEvent;
        if (activeEvent is not null
            && string.Equals(
                activeEvent.SourceId,
                WindowsClockTimerSyncService.SourceId,
                StringComparison.OrdinalIgnoreCase))
        {
            _ = _windowsClockTimerService.TogglePauseAsync(
                activeEvent.EventId);
            return;
        }

        if (activeEvent?.Kind == CapsuleEventKind.Stopwatch)
        {
            ToggleStopwatchPause();
            return;
        }

        _countdownTimerService.TogglePause();
    }

    internal void CancelCountdown()
    {
        var activeEvent = _eventScheduler.ActiveEvent;
        var isTerminalTimer = activeEvent?.Kind == CapsuleEventKind.Timer
                              && activeEvent.TaskState
                              != CapsuleTaskState.Running;
        if (activeEvent is not null
            && string.Equals(
                activeEvent.SourceId,
                WindowsClockTimerSyncService.SourceId,
                StringComparison.OrdinalIgnoreCase))
        {
            _ = _windowsClockTimerService.ResetAsync(activeEvent.EventId);
            if (isTerminalTimer)
            {
                _eventScheduler.Dismiss(activeEvent.EventId);
            }

            return;
        }

        if (activeEvent?.Kind == CapsuleEventKind.Stopwatch)
        {
            if (activeEvent.TaskState == CapsuleTaskState.Running)
            {
                StopStopwatch();
            }
            else
            {
                _eventScheduler.Dismiss(activeEvent.EventId);
            }

            return;
        }

        if (activeEvent is not null && isTerminalTimer)
        {
            _eventScheduler.Dismiss(activeEvent.EventId);
            return;
        }

        _countdownTimerService.Cancel();
    }

    internal void ToggleActiveEventPinned()
    {
        var activeEvent = _eventScheduler.ActiveEvent;
        if (activeEvent is null)
        {
            return;
        }

        if (_eventScheduler.IsPinned(activeEvent.EventId))
        {
            _eventScheduler.Unpin();
        }
        else
        {
            _eventScheduler.Pin(activeEvent.EventId);
        }

        ApplyPinnedPresentation(activeEvent);
    }

    internal DiagnosticsSnapshot CreateDiagnosticsSnapshot(
        string settingsPath,
        StartupRegistrationStatus startupStatus,
        bool isPrimaryInstance)
    {
        var timerStatus = _countdownTimerService.CurrentStatus;
        var localTimerText = timerStatus.IsActive
            ? $"{(timerStatus.IsPaused ? "已暂停" : "运行中")} · "
              + $"剩余 {FormatDiagnosticDuration(timerStatus.Remaining)}"
            : "未运行";
        var timerText = $"内置：{localTimerText}；"
                        + $"Windows：{_windowsClockTimerService.Status}";
        var activeEvent = _eventScheduler.ActiveEvent;
        var secondaryEvent = _eventScheduler.SecondaryEvent;
        var activeEventText = activeEvent is null
            ? "无"
            : $"{activeEvent.Kind} · {activeEvent.Source} · "
              + $"{activeEvent.Priority}"
              + (_eventScheduler.IsPinned(activeEvent.EventId)
                  ? " · 已固定"
                  : string.Empty)
              + (secondaryEvent is null
                  ? string.Empty
                  : $" · 辅助：{secondaryEvent.Kind}/"
                    + secondaryEvent.Source);

        return new DiagnosticsSnapshot(
            $"PID {Environment.ProcessId} · "
            + (isPrimaryInstance ? "主实例" : "辅助实例"),
            _mediaStatus,
            _notificationStatus,
            _localTaskStatus,
            timerText,
            activeEventText,
            $"{settingsPath} · Schema {_settings.SchemaVersion}",
            startupStatus.Message,
            _accessibilityPreferences.Summary);
    }

    private void ApplyVisibilitySuppression()
    {
        var shouldSuppress = !_isPaused
                             && (_settings.HideInFullscreen
                                 && _isFullscreenDetected);
        if (_isSuppressed == shouldSuppress || _windowHandle == nint.Zero)
        {
            return;
        }

        _isSuppressed = shouldSuppress;
        if (shouldSuppress)
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
            return;
        }

        Opacity = 1;
        PositionOnTargetMonitor();
        ApplyRoundedWindowRegion();
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwShowNoActivate);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        if (_isAnimatingSize)
        {
            return;
        }

        PositionOnTargetMonitor();
        ApplyRoundedWindowRegion();
    }

    private void OnCapsuleSurfaceSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        var radius = Math.Min(
            46,
            Math.Max(1, e.NewSize.Height / 2));
        CapsuleSurface.CornerRadius = new CornerRadius(
            radius);
        if (_mediaSnapshot is not null && !_isMediaScrubbing)
        {
            UpdateMediaProgress();
        }
    }

    private void ToggleExpanded()
    {
        if (_isPaused)
        {
            PowerStateChangeRequested?.Invoke(false);
            return;
        }

        if (_activeCapsuleEvent is not null)
        {
            if (_isMediaExpandedOverEvent)
            {
                SetMediaEventExpanded(false);
                return;
            }

            if (IsLongRunningEvent(_activeCapsuleEvent))
            {
                SetLongRunningEventExpanded(!_isEventExpanded);
                return;
            }

            if (_activeCapsuleEvent.Kind == CapsuleEventKind.Timer
                && _activeCapsuleEvent.TaskState
                != CapsuleTaskState.Running)
            {
                return;
            }

            _eventScheduler.Dismiss(_activeCapsuleEvent.EventId);
            SchedulePostDismissalRecovery();
            return;
        }

        _isExpanded = !_isExpanded;
        var animationVersion = ++_presentationAnimationVersion;
        AnimateSurfacePresentation(animationVersion);

        CapsuleSurface.Padding = _isExpanded
            ? MediaExpandedPadding
            : new Thickness(12, 0, 12, 0);

        AnimateExpandedContent(_isExpanded, animationVersion);
        AnimateWindowSize(DesiredWidth, DesiredHeight);
    }

    private void OnEventCollapseTimerTick(object? sender, EventArgs e)
    {
        _eventCollapseTimer.Stop();
        if (_activeCapsuleEvent is not null
            && IsLongRunningEvent(_activeCapsuleEvent)
            && _isEventExpanded)
        {
            SetLongRunningEventExpanded(false);
        }
    }

    private async void SchedulePostDismissalRecovery()
    {
        await Task.Delay(ResizeDuration + TimeSpan.FromMilliseconds(120));
        if (Dispatcher.HasShutdownStarted
            || _activeCapsuleEvent is not null
            || _isPaused
            || _isSuppressed)
        {
            return;
        }

        _presentationAnimationVersion++;
        ClearPrimaryEventAnimations();
        UpdateSurfacePresentation();
        UpdateCompactVisibility();
        AnimateWindowSize(DesiredWidth, DesiredHeight);
        Opacity = 1;
        if (_windowHandle != nint.Zero)
        {
            PositionOnTargetMonitor();
            ApplyRoundedWindowRegion();
            NativeMethods.ShowWindow(
                _windowHandle,
                NativeMethods.SwShowNoActivate);
        }
    }

    private void ScheduleEventCollapse(TimeSpan delay)
    {
        _eventCollapseTimer.Stop();
        _eventCollapseTimer.Interval = delay;
        _eventCollapseTimer.Start();
    }

    private void SetLongRunningEventExpanded(bool expanded)
    {
        if (_activeCapsuleEvent is null
            || !IsLongRunningEvent(_activeCapsuleEvent)
            || _isEventExpanded == expanded)
        {
            return;
        }

        _eventCollapseTimer.Stop();
        var wasMediaEventCompact = IsMediaEventCompact;
        _isEventExpanded = expanded;
        var useMediaEventCompact = expanded
            ? wasMediaEventCompact
            : IsMediaEventCompact;
        var compactContent = useMediaEventCompact
            ? (FrameworkElement)MediaEventCompactContent
            : EventCompactContent;
        var compactContentScale = useMediaEventCompact
            ? MediaEventCompactContentScale
            : EventCompactContentScale;
        if (expanded)
        {
            ScheduleEventCollapse(TimeSpan.FromSeconds(5));
        }

        var animationVersion = ++_presentationAnimationVersion;
        ApplyCompactEvent(
            _activeCapsuleEvent,
            _secondaryCapsuleEvent);
        UpdateSurfacePresentation();

        ClearPrimaryEventAnimations();
        EventAlertContent.Visibility = Visibility.Visible;
        EventCompactContent.Visibility = useMediaEventCompact
            ? Visibility.Collapsed
            : Visibility.Visible;
        MediaEventCompactContent.Visibility = useMediaEventCompact
            ? Visibility.Visible
            : Visibility.Collapsed;

        CapsuleSurface.Padding = expanded
            ? new Thickness(16, 10, 16, 10)
            : new Thickness(10, 0, 10, 0);

        if (!ShouldAnimate)
        {
            EventAlertContent.Opacity = 1;
            EventAlertScale.ScaleX = 1;
            EventAlertScale.ScaleY = 1;
            compactContent.Opacity = 1;
            compactContentScale.ScaleX = 1;
            compactContentScale.ScaleY = 1;
            UpdateCompactVisibility();
            AnimateWindowSize(DesiredWidth, DesiredHeight);
            return;
        }

        var ease = new QuarticEase
        {
            EasingMode = EasingMode.EaseInOut
        };
        var alertOpacity = expanded
            ? new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(210))
            : new DoubleAnimation(
                1,
                0,
                TimeSpan.FromMilliseconds(150));
        var compactOpacity = expanded
            ? new DoubleAnimation(
                1,
                0,
                TimeSpan.FromMilliseconds(135))
            : new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(45)
            };
        alertOpacity.EasingFunction = ease;
        compactOpacity.EasingFunction = ease;
        var alertScale = expanded
            ? new DoubleAnimation(
                0.94,
                1,
                TimeSpan.FromMilliseconds(250))
            : new DoubleAnimation(
                1,
                0.9,
                TimeSpan.FromMilliseconds(180));
        var compactScale = expanded
            ? new DoubleAnimation(
                1,
                0.88,
                TimeSpan.FromMilliseconds(170))
            : new DoubleAnimation(
                0.82,
                1,
                TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(25)
            };
        alertScale.EasingFunction = ease;
        compactScale.EasingFunction = ease;

        var completionAnimation = expanded
            ? alertOpacity
            : compactOpacity;
        completionAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _presentationAnimationVersion)
            {
                return;
            }

            ClearPrimaryEventAnimations();
            UpdateCompactVisibility();
        };

        EventAlertContent.BeginAnimation(
            OpacityProperty,
            alertOpacity,
            HandoffBehavior.SnapshotAndReplace);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            alertScale,
            HandoffBehavior.SnapshotAndReplace);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            alertScale,
            HandoffBehavior.SnapshotAndReplace);
        compactContent.BeginAnimation(
            OpacityProperty,
            compactOpacity,
            HandoffBehavior.SnapshotAndReplace);
        compactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            compactScale,
            HandoffBehavior.SnapshotAndReplace);
        compactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            compactScale,
            HandoffBehavior.SnapshotAndReplace);
        AnimateWindowSize(DesiredWidth, DesiredHeight);
    }

    private void SetMediaEventExpanded(bool expanded)
    {
        if (_activeCapsuleEvent is null
            || !IsLongRunningEvent(_activeCapsuleEvent)
            || _mediaSnapshot is null
            || _isMediaExpandedOverEvent == expanded)
        {
            return;
        }

        _eventCollapseTimer.Stop();
        var animationVersion = ++_presentationAnimationVersion;
        _isMediaExpandedOverEvent = expanded;
        _isEventExpanded = false;
        _isExpanded = false;
        AnimateSurfacePresentation(animationVersion);

        var expandedWasVisible = ExpandedContent.Visibility
                                 == Visibility.Visible;
        var compactWasVisible = MediaEventCompactContent.Visibility
                                == Visibility.Visible;
        var expandedStartOpacity = expandedWasVisible
            ? ExpandedContent.Opacity
            : 0;
        var compactStartOpacity = compactWasVisible
            ? MediaEventCompactContent.Opacity
            : 0;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContentTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        MediaEventCompactContent.BeginAnimation(OpacityProperty, null);
        MediaEventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        MediaEventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        ExpandedContent.Opacity = expandedStartOpacity;
        ExpandedContentTranslate.Y = 0;
        MediaEventCompactContent.Opacity = compactStartOpacity;
        MediaEventCompactContentScale.ScaleX = 1;
        MediaEventCompactContentScale.ScaleY = 1;

        CompactContent.Visibility = Visibility.Collapsed;
        MediaCompactContent.Visibility = Visibility.Collapsed;
        EventCompactContent.Visibility = Visibility.Collapsed;
        EventAlertContent.Visibility = Visibility.Collapsed;
        SecondaryEventContent.Visibility = Visibility.Collapsed;
        PowerOffContent.Visibility = Visibility.Collapsed;
        MediaEventCompactContent.Visibility = Visibility.Visible;
        ExpandedContent.Visibility = Visibility.Visible;

        CapsuleSurface.Padding = expanded
            ? MediaExpandedPadding
            : new Thickness(10, 0, 10, 0);

        if (!ShouldAnimate)
        {
            ExpandedContent.Opacity = expanded ? 1 : 0;
            MediaEventCompactContent.Opacity = expanded ? 0 : 1;
            UpdateCompactVisibility();
            AnimateWindowSize(DesiredWidth, DesiredHeight);
            return;
        }

        var easeOut = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        var crossfadeDelay = TimeSpan.FromMilliseconds(expanded ? 45 : 25);
        var crossfadeDuration = TimeSpan.FromMilliseconds(185);
        var expandedOpacity = new DoubleAnimation(
            expandedStartOpacity,
            expanded ? 1 : 0,
            crossfadeDuration)
        {
            BeginTime = crossfadeDelay,
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        var compactOpacity = new DoubleAnimation(
            compactStartOpacity,
            expanded ? 0 : 1,
            crossfadeDuration)
        {
            BeginTime = crossfadeDelay,
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        var completionAnimation = expanded
            ? expandedOpacity
            : compactOpacity;
        completionAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _presentationAnimationVersion)
            {
                return;
            }

            ExpandedContent.BeginAnimation(OpacityProperty, null);
            MediaEventCompactContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 1;
            MediaEventCompactContent.Opacity = 1;
            UpdateCompactVisibility();
        };

        AnimateWindowSize(DesiredWidth, DesiredHeight);
        ExpandedContent.BeginAnimation(
            OpacityProperty,
            expandedOpacity,
            HandoffBehavior.SnapshotAndReplace);
        MediaEventCompactContent.BeginAnimation(
            OpacityProperty,
            compactOpacity,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateWindowSize(double targetWidth, double targetHeight)
    {
        var startWidth = CapsuleSurface.ActualWidth > 0
            ? CapsuleSurface.ActualWidth
            : ActualWidth;
        var startHeight = CapsuleSurface.ActualHeight > 0
            ? CapsuleSurface.ActualHeight
            : ActualHeight;
        var animationVersion = ++_sizeAnimationVersion;

        StopSurfaceResizeAnimation();
        var currentStashOffset = -Math.Max(
            0,
            startHeight - TopStashVisibleHeight);
        var surfaceIsAlreadyStashed = Math.Abs(
            CapsuleSurfaceTranslate.Y - currentStashOffset) < 1;
        if (ShouldStashSurface && surfaceIsAlreadyStashed)
        {
            _isAnimatingSize = false;
            CapsuleSurface.HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center;
            CapsuleSurface.VerticalAlignment =
                System.Windows.VerticalAlignment.Top;
            CapsuleSurface.Width = targetWidth;
            CapsuleSurface.Height = targetHeight;
            Width = HostWidth;
            Height = HostHeight;
            UpdateTopStashPresentation(animate: false, targetHeight);
            PositionOnTargetMonitor();
            return;
        }

        UpdateTopStashPresentation(ShouldAnimate, targetHeight);

        if (!ShouldAnimate
            || (Math.Abs(startWidth - targetWidth) < 0.5
                && Math.Abs(startHeight - targetHeight) < 0.5))
        {
            _isAnimatingSize = false;
            CapsuleSurfaceScale.ScaleX = 1;
            CapsuleSurfaceScale.ScaleY = 1;
            CapsuleSurface.HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center;
            CapsuleSurface.VerticalAlignment =
                System.Windows.VerticalAlignment.Top;
            CapsuleSurface.Width = targetWidth;
            CapsuleSurface.Height = targetHeight;
            Width = HostWidth;
            Height = HostHeight;
            PositionOnTargetMonitor();
            return;
        }

        CapsuleSurface.HorizontalAlignment =
            System.Windows.HorizontalAlignment.Center;
        CapsuleSurface.VerticalAlignment =
            System.Windows.VerticalAlignment.Top;
        CapsuleSurface.Width = startWidth;
        CapsuleSurface.Height = startHeight;
        _isAnimatingSize = true;
        Width = HostWidth;
        Height = HostHeight;
        PositionOnTargetMonitor();

        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        var widthAnimation = new DoubleAnimation(
            startWidth,
            targetWidth,
            ResizeDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var heightAnimation = new DoubleAnimation(
            startHeight,
            targetHeight,
            ResizeDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _sizeAnimationVersion)
            {
                return;
            }

            CapsuleSurface.Width = targetWidth;
            CapsuleSurface.Height = targetHeight;
            CapsuleSurface.BeginAnimation(
                FrameworkElement.WidthProperty,
                null);
            CapsuleSurface.BeginAnimation(
                FrameworkElement.HeightProperty,
                null);
            Width = HostWidth;
            Height = HostHeight;
            CapsuleSurface.HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center;
            CapsuleSurface.VerticalAlignment =
                System.Windows.VerticalAlignment.Top;
            CapsuleSurface.Width = targetWidth;
            CapsuleSurface.Height = targetHeight;
            _isAnimatingSize = false;
            PositionOnTargetMonitor();
        };

        CapsuleSurface.BeginAnimation(
            FrameworkElement.WidthProperty,
            widthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        CapsuleSurface.BeginAnimation(
            FrameworkElement.HeightProperty,
            heightAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopSurfaceResizeAnimation()
    {
        var currentWidth = CapsuleSurface.ActualWidth;
        var currentHeight = CapsuleSurface.ActualHeight;
        if (_isAnimatingSize && currentWidth > 0 && currentHeight > 0)
        {
            CapsuleSurface.Width = currentWidth;
            CapsuleSurface.Height = currentHeight;
        }
        CapsuleSurface.BeginAnimation(
            FrameworkElement.WidthProperty,
            null);
        CapsuleSurface.BeginAnimation(
            FrameworkElement.HeightProperty,
            null);
        CapsuleSurfaceScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        CapsuleSurfaceScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        CapsuleSurfaceScale.ScaleX = 1;
        CapsuleSurfaceScale.ScaleY = 1;
        _isAnimatingSize = false;
    }

    private void UpdateTopStashPresentation(
        bool animate,
        double targetHeight)
    {
        var shouldStash = ShouldStashSurface;
        var targetOffset = shouldStash
            ? -Math.Max(0, targetHeight - TopStashVisibleHeight)
            : _settings.TopGap;
        var useMediaSideArrow = !shouldStash && UseMediaSideArrow;
        var useGenericSideArrow = !shouldStash && UseGenericSideArrow;
        TopStashArrowButton.Visibility = useGenericSideArrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaTopStashArrowButton.Visibility = useMediaSideArrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        CapsuleSurface.Padding = GetCapsulePadding();
        TopStashHandleTranslate.X = UseGenericSideArrow
            ? SideArrowLaneWidth / 2
            : 0;

        var animationVersion = ++_topStashAnimationVersion;
        var currentOffset = CapsuleSurfaceTranslate.Y;
        CapsuleSurfaceTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        CapsuleSurfaceTranslate.Y = currentOffset;

        if (!animate || Math.Abs(currentOffset - targetOffset) < 0.5)
        {
            CapsuleSurfaceTranslate.Y = targetOffset;
            TopStashHandle.Visibility = shouldStash
                ? Visibility.Visible
                : Visibility.Collapsed;
            _isTopStashAnimating = false;
            return;
        }

        _isTopStashAnimating = true;
        TopStashHandle.Visibility = shouldStash
            ? Visibility.Collapsed
            : Visibility.Visible;

        var animation = new DoubleAnimation(
            currentOffset,
            targetOffset,
            TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (animationVersion != _topStashAnimationVersion)
            {
                return;
            }

            CapsuleSurfaceTranslate.Y = targetOffset;
            CapsuleSurfaceTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            TopStashHandle.Visibility = shouldStash
                ? Visibility.Visible
                : Visibility.Collapsed;
            _isTopStashAnimating = false;
        };
        CapsuleSurfaceTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private Thickness GetCapsulePadding()
    {
        Thickness padding;
        if (_isQuickMenuOpen)
        {
            padding = new Thickness(10, 6, 10, 6);
        }
        else if (_activeCapsuleEvent is not null)
        {
            padding = _isMediaExpandedOverEvent
                ? MediaExpandedPadding
                : IsCompactActiveEvent
                    ? new Thickness(10, 0, 10, 0)
                    : new Thickness(16, 10, 16, 10);
        }
        else
        {
            padding = _isExpanded
                ? MediaExpandedPadding
                : new Thickness(12, 0, 12, 0);
        }

        return UseGenericSideArrow
            ? new Thickness(
                padding.Left,
                padding.Top,
                padding.Right + SideArrowLaneWidth,
                padding.Bottom)
            : padding;
    }

    private void AnimateExpandedContent(
        bool expanding,
        int animationVersion)
    {
        var compactContent = _mediaSnapshot is null
            ? (FrameworkElement)CompactContent
            : MediaCompactContent;
        var compactScale = _mediaSnapshot is null
            ? CompactContentScale
            : MediaCompactContentScale;
        var otherCompactContent = _mediaSnapshot is null
            ? (FrameworkElement)MediaCompactContent
            : CompactContent;
        var expandedWasVisible = ExpandedContent.Visibility
                                 == Visibility.Visible;
        var compactWasVisible = compactContent.Visibility
                                == Visibility.Visible;
        var expandedStartOpacity = expandedWasVisible
            ? ExpandedContent.Opacity
            : 0;
        var compactStartOpacity = compactWasVisible
            ? compactContent.Opacity
            : 0;

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContentTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        CompactContent.BeginAnimation(OpacityProperty, null);
        MediaCompactContent.BeginAnimation(OpacityProperty, null);
        CompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        CompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        MediaCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        MediaCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        ExpandedContent.Opacity = expandedStartOpacity;
        ExpandedContentTranslate.Y = 0;
        compactContent.Opacity = compactStartOpacity;
        compactScale.ScaleX = 1;
        compactScale.ScaleY = 1;

        otherCompactContent.Visibility = Visibility.Collapsed;
        compactContent.Visibility = Visibility.Visible;
        ExpandedContent.Visibility = Visibility.Visible;

        if (!ShouldAnimate)
        {
            ExpandedContent.Opacity = expanding ? 1 : 0;
            compactContent.Opacity = expanding ? 0 : 1;
            ExpandedContent.Visibility = expanding
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateCompactVisibility();
            return;
        }

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var crossfadeDelay = TimeSpan.FromMilliseconds(expanding ? 45 : 25);
        var crossfadeDuration = TimeSpan.FromMilliseconds(185);
        var expandedOpacity = new DoubleAnimation(
            expandedStartOpacity,
            expanding ? 1 : 0,
            crossfadeDuration)
        {
            BeginTime = crossfadeDelay,
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        var compactOpacity = new DoubleAnimation(
            compactStartOpacity,
            expanding ? 0 : 1,
            crossfadeDuration)
        {
            BeginTime = crossfadeDelay,
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        var completionAnimation = expanding
            ? expandedOpacity
            : compactOpacity;
        completionAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _presentationAnimationVersion)
            {
                return;
            }

            ExpandedContent.BeginAnimation(OpacityProperty, null);
            compactContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 1;
            compactContent.Opacity = 1;
            ExpandedContent.Visibility = expanding
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateCompactVisibility();
        };

        ExpandedContent.BeginAnimation(
            OpacityProperty,
            expandedOpacity,
            HandoffBehavior.SnapshotAndReplace);
        compactContent.BeginAnimation(
            OpacityProperty,
            compactOpacity,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateSurfacePresentation(int animationVersion)
    {
        var resourceKey = !_isPaused
                          && ((_activeCapsuleEvent is not null
                               && !IsCompactActiveEvent)
                              || _isExpanded)
            ? "CapsuleSurfaceBrush"
            : "CompactCapsuleSurfaceBrush";
        if (!ShouldAnimate
            || TryFindResource(resourceKey) is not SolidColorBrush targetBrush
            || CapsuleSurface.Background is not SolidColorBrush currentBrush)
        {
            UpdateSurfacePresentation();
            return;
        }

        var animatedBrush = new SolidColorBrush(currentBrush.Color);
        CapsuleSurface.Background = animatedBrush;
        var colorAnimation = new ColorAnimation(
            currentBrush.Color,
            targetBrush.Color,
            TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.HoldEnd
        };
        colorAnimation.Completed += (_, _) =>
        {
            if (animationVersion == _presentationAnimationVersion)
            {
                CapsuleSurface.SetResourceReference(
                    Border.BackgroundProperty,
                    resourceKey);
            }
        };
        animatedBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            colorAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void OnWindowMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_suppressNextCapsuleMouseLeftButtonUp)
        {
            _suppressNextCapsuleMouseLeftButtonUp = false;
            e.Handled = true;
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindVisualAncestor<Button>(source) is not null
            || IsVisualDescendantOf(source, MediaProgressHitTarget))
        {
            return;
        }

        if (_isQuickMenuOpen)
        {
            SetQuickMenuOpen(false);
            e.Handled = true;
            return;
        }

        if (IsMediaEventCompact
            && IsVisualDescendantOf(
                source,
                MediaEventCompactMusicPanel))
        {
            SetMediaEventExpanded(true);
        }
        else if (IsMediaEventCompact
                 && IsVisualDescendantOf(
                     source,
                     MediaEventCompactActivityPanel))
        {
            SetLongRunningEventExpanded(true);
        }
        else if (_secondaryCapsuleEvent is not null
            && IsVisualDescendantOf(source, SecondaryEventContent))
        {
            _eventScheduler.Pin(_secondaryCapsuleEvent.EventId);
        }
        else
        {
            ToggleExpanded();
        }

        e.Handled = true;
    }

    private void OnPreviousButtonClick(object sender, RoutedEventArgs e)
    {
        _ = _mediaService.GoToPreviousAsync();
    }

    private void OnPlayPauseButtonClick(object sender, RoutedEventArgs e)
    {
        _ = _mediaService.TogglePlayPauseAsync();
    }

    private void OnNextButtonClick(object sender, RoutedEventArgs e)
    {
        _ = _mediaService.GoToNextAsync();
    }

    private void OnMediaProgressPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_mediaSnapshot is null
            || GetEffectiveMediaDuration(_mediaSnapshot) <= TimeSpan.Zero)
        {
            MediaProgressHitTarget.ToolTip = "正在获取歌曲时长";
            e.Handled = true;
            return;
        }

        _isMediaScrubbing = true;
        MediaProgressHitTarget.CaptureMouse();
        SetMediaProgressFromPointer(
            e.GetPosition(MediaProgressHitTarget));
        e.Handled = true;
    }

    private void OnMediaProgressPreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMediaScrubbing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SetMediaProgressFromPointer(
            e.GetPosition(MediaProgressHitTarget));
        e.Handled = true;
    }

    private async void OnMediaProgressPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isMediaScrubbing)
        {
            return;
        }

        SetMediaProgressFromPointer(
            e.GetPosition(MediaProgressHitTarget));
        var target = TimeSpan.FromSeconds(_mediaScrubPositionSeconds);
        _isMediaScrubbing = false;
        MediaProgressHitTarget.ReleaseMouseCapture();
        e.Handled = true;
        if (_mediaSnapshot is null)
        {
            UpdateMediaProgress();
            return;
        }

        _timelineAnchorPosition = ClampPlaybackPosition(
            target,
            GetEffectiveMediaDuration(_mediaSnapshot));
        _timelineAnchorCapturedAt = DateTimeOffset.UtcNow;
        _pendingMediaSeekPosition = target;
        _pendingMediaSeekUntil = DateTimeOffset.UtcNow
                                 + TimeSpan.FromSeconds(1.4);
        _pendingMediaSeekTrackIdentity = BuildMediaTrackIdentity(
            _mediaSnapshot);
        UpdateMediaProgress();
        var succeeded = await _mediaService.SeekAsync(target);
        if (!succeeded)
        {
            ClearPendingMediaSeek();
            if (_mediaSnapshot is not null)
            {
                UpdateTimelineAnchor(_mediaSnapshot);
            }
            MediaProgressHitTarget.ToolTip =
                "当前播放器不支持进度跳转";
            UpdateMediaProgress();
            return;
        }

        ClearPendingMediaSeek();
        MediaProgressHitTarget.ToolTip = "点击或拖动以调整播放位置";
        if (_mediaSnapshot is not null)
        {
            UpdateTimelineAnchor(_mediaSnapshot);
        }
        UpdateMediaProgress();
    }

    private void OnMediaProgressLostMouseCapture(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMediaScrubbing)
        {
            return;
        }

        _isMediaScrubbing = false;
        UpdateMediaProgress();
    }

    private void SetMediaProgressFromPointer(Point pointerPosition)
    {
        var width = MediaProgressHitTarget.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(pointerPosition.X / width, 0, 1);
        _mediaScrubPositionSeconds = _mediaProgressDurationSeconds * ratio;
        var duration = TimeSpan.FromSeconds(_mediaProgressDurationSeconds);
        var position = TimeSpan.FromSeconds(_mediaScrubPositionSeconds);
        UpdateMediaProgressVisual(position, duration);
        UpdateMediaProgressLabels(position, duration);
    }

    private void OnPowerToggleButtonClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
    }

    private void OnTimerPauseResumeButtonClick(
        object sender,
        RoutedEventArgs e)
    {
        ToggleCountdownPause();
    }

    private void OnTimerCancelButtonClick(object sender, RoutedEventArgs e)
    {
        CancelCountdown();
    }

    private void OnCapsulePreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_isSuppressed || !CapsuleSurface.IsVisible)
        {
            return;
        }

        if (ShouldStashSurface)
        {
            e.Handled = true;
            return;
        }

        SetQuickMenuOpen(true);
        e.Handled = true;
    }

    private void OnCapsulePreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!ShouldStashSurface)
        {
            return;
        }

        _suppressNextCapsuleMouseLeftButtonUp = true;
        if (!_isTopStashAnimating)
        {
            TopStashChangeRequested?.Invoke(false);
        }

        e.Handled = true;
    }

    private void OnTopStashArrowClick(object sender, RoutedEventArgs e)
    {
        if (_isTopStashAnimating || _isTopStashed)
        {
            return;
        }

        TopStashChangeRequested?.Invoke(true);
        e.Handled = true;
    }

    private void OnQuickMenuCloseClick(object sender, RoutedEventArgs e)
    {
        SetQuickMenuOpen(false);
    }

    private void OnContextToggleClick(object sender, RoutedEventArgs e)
    {
        SetQuickMenuOpen(false, animateSize: false);
        ToggleExpanded();
    }

    private void OnContextDoNotDisturbClick(
        object sender,
        RoutedEventArgs e)
    {
        DoNotDisturbChangeRequested?.Invoke(!_settings.DoNotDisturb);
    }

    private void OnContextCountdownClick(object sender, RoutedEventArgs e)
    {
        SetQuickMenuOpen(false);
        CountdownRequested?.Invoke(null);
    }

    private void OnContextCountdownFiveClick(
        object sender,
        RoutedEventArgs e)
    {
        SetQuickMenuOpen(false, animateSize: false);
        CountdownRequested?.Invoke(5);
    }

    private void OnContextCountdownFifteenClick(
        object sender,
        RoutedEventArgs e)
    {
        SetQuickMenuOpen(false, animateSize: false);
        CountdownRequested?.Invoke(15);
    }

    private void OnContextCountdownTwentyFiveClick(
        object sender,
        RoutedEventArgs e)
    {
        SetQuickMenuOpen(false, animateSize: false);
        CountdownRequested?.Invoke(25);
    }

    private void OnContextWindowsStopwatchClick(
        object sender,
        RoutedEventArgs e)
    {
        SetQuickMenuOpen(false);
        _ = _windowsClockTimerService.OpenStopwatchAsync();
    }

    private void OnContextStopwatchStartClick(
        object sender,
        RoutedEventArgs e)
    {
        StartStopwatch();
    }

    private void OnContextStopwatchPauseResumeClick(
        object sender,
        RoutedEventArgs e)
    {
        ToggleStopwatchPause();
    }

    private void OnContextStopwatchStopClick(
        object sender,
        RoutedEventArgs e)
    {
        StopStopwatch();
    }

    private void OnContextSettingsClick(object sender, RoutedEventArgs e)
    {
        SetQuickMenuOpen(false);
        SettingsRequested?.Invoke();
    }

    private void OnContextExitClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
    }

    private void SetQuickMenuOpen(
        bool isOpen,
        bool animateSize = true)
    {
        if (_isQuickMenuOpen == isOpen
            || (isOpen
                && (_isSuppressed
                    || !IsLoaded
                    || !CapsuleSurface.IsVisible)))
        {
            return;
        }

        _isQuickMenuOpen = isOpen;
        _presentationAnimationVersion++;
        QuickMenuContent.BeginAnimation(OpacityProperty, null);
        QuickMenuTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        QuickMenuContent.Opacity = isOpen ? 0 : 1;
        QuickMenuTranslate.Y = isOpen ? -6 : 0;

        if (isOpen)
        {
            QuickToggleButton.Content = GetQuickToggleLabel();
            UpdateQuickDoNotDisturbButton();
            CapsuleSurface.Padding = new Thickness(10, 6, 10, 6);
        }
        else if (_activeCapsuleEvent is not null)
        {
            CapsuleSurface.Padding = _isMediaExpandedOverEvent
                ? MediaExpandedPadding
                : IsCompactActiveEvent
                ? new Thickness(10, 0, 10, 0)
                : new Thickness(16, 10, 16, 10);
        }
        else
        {
            CapsuleSurface.Padding = _isExpanded
                ? MediaExpandedPadding
                : new Thickness(12, 0, 12, 0);
        }

        UpdateSurfacePresentation();
        UpdateCompactVisibility();
        if (animateSize)
        {
            AnimateWindowSize(DesiredWidth, DesiredHeight);
        }

        if (!isOpen)
        {
            QuickMenuContent.Opacity = 0;
            QuickMenuTranslate.Y = -6;
            return;
        }

        QuickMenuContent.Visibility = Visibility.Visible;
        if (!ShouldAnimate)
        {
            QuickMenuContent.Opacity = 1;
            QuickMenuTranslate.Y = 0;
            return;
        }

        var ease = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        QuickMenuContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(170))
            {
                BeginTime = TimeSpan.FromMilliseconds(70),
                EasingFunction = ease
            },
            HandoffBehavior.SnapshotAndReplace);
        QuickMenuTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(
                -6,
                0,
                TimeSpan.FromMilliseconds(190))
            {
                BeginTime = TimeSpan.FromMilliseconds(55),
                EasingFunction = ease
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private string GetQuickToggleLabel()
    {
        if (_activeCapsuleEvent is not null)
        {
            return _isEventExpanded || _isMediaExpandedOverEvent
                ? "收起当前"
                : "展开当前";
        }

        return _isExpanded ? "收起胶囊" : "展开胶囊";
    }

    private void UpdateQuickDoNotDisturbButton()
    {
        QuickDoNotDisturbButton.Content = _settings.DoNotDisturb
            ? "勿扰：开"
            : "勿扰：关";
        QuickDoNotDisturbButton.ToolTip = _settings.DoNotDisturb
            ? "胶囊事件弹窗已暂停；点击关闭胶囊勿扰"
            : "胶囊事件弹窗正常显示；点击开启胶囊勿扰";
    }

    private void OnMediaSnapshotChanged(MediaSnapshot? snapshot)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => ApplyMediaSnapshot(snapshot));
    }

    private void OnMediaStatusChanged(string status)
    {
        _mediaStatus = status;

        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => UpdateMediaStatusText());
    }

    private void OnNotificationReceived(NotificationSnapshot snapshot)
    {
        var decision = _eventPolicyEngine.Evaluate(
            CapsuleEvent.FromNotification(snapshot),
            _settings);
        if (decision.CapsuleEvent is not { } allowedEvent
            || _isPaused
            || _settings.DoNotDisturb
            || _isSuppressed
            || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () =>
            {
                if (_isPaused
                    || _settings.DoNotDisturb
                    || _isSuppressed
                    || _windowHandle == nint.Zero)
                {
                    return;
                }

                _eventScheduler.Publish(allowedEvent);
            });
    }

    private void OnNotificationRemoved(uint notificationId)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => _eventScheduler.Remove(
                $"notification:{notificationId}"));
    }

    private void OnNotificationStatusChanged(
        NotificationListenerStatus status,
        string message)
    {
        _notificationStatus = message;
        NotificationStatusChanged?.Invoke(status, message);

        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => UpdateMediaStatusText());
    }

    private void OnLocalTaskEventReceived(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnLocalTaskStatusChanged(string status)
    {
        _localTaskStatus = status;
    }

    private void OnCountdownEventChanged(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnCountdownStatusChanged(CountdownTimerStatus status)
    {
        CountdownStatusChanged?.Invoke(status);
    }

    private void OnStopwatchEventChanged(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnStopwatchStatusChanged(StopwatchStatus status)
    {
        StopwatchStatusChanged?.Invoke(status);
    }

    private void OnWindowsClockTimerEventChanged(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnWindowsClockTimerEventRemoved(string eventId)
    {
        _eventScheduler.Remove(eventId);
    }

    private void OnConnectivityEventReceived(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnBrowserDownloadEventChanged(CapsuleEvent capsuleEvent)
    {
        PublishWithPolicy(capsuleEvent);
    }

    private void OnBrowserDownloadEventRemoved(string eventId)
    {
        _eventScheduler.Remove(eventId);
    }

    private void PublishWithPolicy(CapsuleEvent capsuleEvent)
    {
        var decision = _eventPolicyEngine.Evaluate(capsuleEvent, _settings);
        if (decision.CapsuleEvent is { } allowedEvent)
        {
            _eventScheduler.Publish(allowedEvent);
        }
    }

    private void OnPresentedCapsuleEventsChanged(
        CapsuleEvent? primaryEvent,
        CapsuleEvent? secondaryEvent)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => ApplyPresentedCapsuleEvents(
                primaryEvent,
                secondaryEvent));
    }

    private void ApplyPresentedCapsuleEvents(
        CapsuleEvent? primaryEvent,
        CapsuleEvent? secondaryEvent)
    {
        var previousPrimaryEvent = _activeCapsuleEvent;
        var previousSecondaryEvent = _secondaryCapsuleEvent;
        var wasCompactPrimary = IsCompactActiveEvent;
        var wasMediaCompactPrimary = IsMediaEventCompact;
        var wasMediaExpandedOverEvent = _isMediaExpandedOverEvent;
        var isSamePrimary = HasSameEventIdentity(
                                previousPrimaryEvent,
                                primaryEvent)
                            && (previousPrimaryEvent is null
                                || primaryEvent is null
                                || previousPrimaryEvent.Kind
                                == primaryEvent.Kind
                                && previousPrimaryEvent.TaskState
                                == primaryEvent.TaskState);
        var isSameSecondary = HasSameEventIdentity(
            previousSecondaryEvent,
            secondaryEvent);

        if (!isSamePrimary)
        {
            _eventCollapseTimer.Stop();
            _isMediaExpandedOverEvent = false;
            _isEventExpanded = IsLongRunningEvent(primaryEvent);
            if (_isEventExpanded)
            {
                ScheduleEventCollapse(TimeSpan.FromMilliseconds(2400));
            }
        }

        _activeCapsuleEvent = primaryEvent;
        _secondaryCapsuleEvent = secondaryEvent;
        UpdateSurfacePresentation();

        if (primaryEvent is null)
        {
            _eventCollapseTimer.Stop();
            _isEventExpanded = false;
            StopEventGlyphAnimation();
            PinnedIndicatorText.Visibility = Visibility.Collapsed;
            EventActionPanel.Visibility = Visibility.Collapsed;
            SecondaryEventContent.Visibility = Visibility.Collapsed;
            EventAlertContent.Margin = new Thickness(0);
            CapsuleSurface.Padding = _isExpanded
                ? MediaExpandedPadding
                : new Thickness(12, 0, 12, 0);

            if (previousPrimaryEvent is not null && ShouldAnimate)
            {
                if (wasMediaExpandedOverEvent)
                {
                    var animationVersion = ++_presentationAnimationVersion;
                    AnimateSurfacePresentation(animationVersion);
                    AnimateExpandedContent(false, animationVersion);
                }
                else if (wasCompactPrimary)
                {
                    AnimateCompactEventDismissal(
                        wasMediaCompactPrimary);
                }
                else
                {
                    AnimatePrimaryEventDismissal();
                }
            }
            else
            {
                _presentationAnimationVersion++;
                ClearPrimaryEventAnimations();
                UpdateCompactVisibility();
            }

            AnimateWindowSize(DesiredWidth, DesiredHeight);
            ActiveEventStatusChanged?.Invoke(false, false);
            return;
        }

        _isExpanded = false;
        EventSourceText.Text = primaryEvent.Source;
        EventTitleText.Text = primaryEvent.Title;
        EventBodyText.Text = string.IsNullOrWhiteSpace(primaryEvent.Message)
            ? GetDefaultEventMessage(primaryEvent)
            : primaryEvent.Message;

        ApplyEventGlyph(primaryEvent);
        ApplyEventProgress(primaryEvent);
        ApplyEventPrivacyLayout(primaryEvent);
        ApplyEventActions(primaryEvent);
        ApplyPinnedPresentation(primaryEvent);
        ApplySecondaryEvent(secondaryEvent);
        ApplyCompactEvent(primaryEvent, secondaryEvent);
        UpdateCompactVisibility();
        CapsuleSurface.Padding = IsCompactActiveEvent
            ? new Thickness(10, 0, 10, 0)
            : new Thickness(16, 10, 16, 10);

        if (!isSamePrimary)
        {
            AnimatePrimaryEventContent();
        }

        if (!isSameSecondary && !IsCompactActiveEvent)
        {
            AnimateSecondaryEventContent(secondaryEvent is not null);
        }

        if (Math.Abs(ActualWidth - DesiredWidth) >= 0.5
            || Math.Abs(ActualHeight - DesiredHeight) >= 0.5)
        {
            AnimateWindowSize(DesiredWidth, DesiredHeight);
        }
    }

    private void AnimatePrimaryEventContent()
    {
        _presentationAnimationVersion++;
        ClearPrimaryEventAnimations();
        CompactContent.Visibility = Visibility.Collapsed;
        MediaCompactContent.Visibility = Visibility.Collapsed;
        EventCompactContent.Visibility = Visibility.Collapsed;
        MediaEventCompactContent.Visibility = Visibility.Collapsed;
        EventAlertContent.Visibility = Visibility.Visible;

        if (!ShouldAnimate)
        {
            EventAlertContent.Opacity = 1;
            EventAlertScale.ScaleX = 1;
            EventAlertScale.ScaleY = 1;
            EventAlertTranslate.Y = 0;
            return;
        }

        EventAlertContent.Opacity = 0;
        EventAlertScale.ScaleX = 0.88;
        EventAlertScale.ScaleY = 0.88;
        EventAlertTranslate.Y = -8;

        EventAlertContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(185))
            {
                BeginTime = TimeSpan.FromMilliseconds(35),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        var contentSpring = new BackEase
        {
            Amplitude = 0.16,
            EasingMode = EasingMode.EaseOut
        };
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(310))
            {
                EasingFunction = contentSpring,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(310))
            {
                EasingFunction = contentSpring,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        EventAlertTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(270))
            {
                EasingFunction = new QuinticEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimatePrimaryEventDismissal()
    {
        var animationVersion = ++_presentationAnimationVersion;
        ClearPrimaryEventAnimations();

        var compactContent = _mediaSnapshot is null
            ? (FrameworkElement)CompactContent
            : MediaCompactContent;
        var compactScale = _mediaSnapshot is null
            ? CompactContentScale
            : MediaCompactContentScale;

        ExpandedContent.Visibility = Visibility.Collapsed;
        CompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaCompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        EventAlertContent.Visibility = Visibility.Visible;

        EventAlertContent.Opacity = 1;
        EventAlertScale.ScaleX = 1;
        EventAlertScale.ScaleY = 1;
        EventAlertTranslate.Y = 0;
        compactContent.Opacity = 0;
        compactScale.ScaleX = 0.9;
        compactScale.ScaleY = 0.9;

        EventAlertContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.94, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.94, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        EventAlertTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        compactContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(130),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        var compactSpring = new BackEase
        {
            Amplitude = 0.12,
            EasingMode = EasingMode.EaseOut
        };
        var compactScaleXAnimation =
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(180))
            {
                BeginTime = TimeSpan.FromMilliseconds(110),
                EasingFunction = compactSpring,
                FillBehavior = FillBehavior.HoldEnd
            };
        compactScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            compactScaleXAnimation,
            HandoffBehavior.SnapshotAndReplace);

        var compactScaleYAnimation =
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(180))
            {
                BeginTime = TimeSpan.FromMilliseconds(110),
                EasingFunction = compactSpring,
                FillBehavior = FillBehavior.HoldEnd
            };
        compactScaleYAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _presentationAnimationVersion
                || _activeCapsuleEvent is not null)
            {
                return;
            }

            ClearPrimaryEventAnimations();
            EventAlertContent.Visibility = Visibility.Collapsed;
            UpdateCompactVisibility();
        };
        compactScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            compactScaleYAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCompactEventDismissal(
        bool wasMediaEventCompact)
    {
        var animationVersion = ++_presentationAnimationVersion;
        ClearPrimaryEventAnimations();

        var compactContent = _mediaSnapshot is null
            ? (FrameworkElement)CompactContent
            : MediaCompactContent;
        var compactScale = _mediaSnapshot is null
            ? CompactContentScale
            : MediaCompactContentScale;
        var eventCompactContent = wasMediaEventCompact
            ? (FrameworkElement)MediaEventCompactContent
            : EventCompactContent;
        var eventCompactScale = wasMediaEventCompact
            ? MediaEventCompactContentScale
            : EventCompactContentScale;

        ExpandedContent.Visibility = Visibility.Collapsed;
        CompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaCompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        EventAlertContent.Visibility = Visibility.Collapsed;
        EventCompactContent.Visibility = wasMediaEventCompact
            ? Visibility.Collapsed
            : Visibility.Visible;
        MediaEventCompactContent.Visibility = wasMediaEventCompact
            ? Visibility.Visible
            : Visibility.Collapsed;

        eventCompactContent.Opacity = 1;
        eventCompactScale.ScaleX = 1;
        eventCompactScale.ScaleY = 1;
        compactContent.Opacity = 0;
        compactScale.ScaleX = 0.9;
        compactScale.ScaleY = 0.9;

        var fadeOut = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        eventCompactContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = fadeOut,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        eventCompactScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.86, TimeSpan.FromMilliseconds(175))
            {
                EasingFunction = fadeOut,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        eventCompactScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.86, TimeSpan.FromMilliseconds(175))
            {
                EasingFunction = fadeOut,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        compactContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                BeginTime = TimeSpan.FromMilliseconds(75),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        var settle = new BackEase
        {
            Amplitude = 0.08,
            EasingMode = EasingMode.EaseOut
        };
        compactScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(190))
            {
                BeginTime = TimeSpan.FromMilliseconds(65),
                EasingFunction = settle,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        var scaleYAnimation =
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(190))
            {
                BeginTime = TimeSpan.FromMilliseconds(65),
                EasingFunction = settle,
                FillBehavior = FillBehavior.HoldEnd
            };
        scaleYAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _presentationAnimationVersion
                || _activeCapsuleEvent is not null)
            {
                return;
            }

            ClearPrimaryEventAnimations();
            EventCompactContent.Visibility = Visibility.Collapsed;
            MediaEventCompactContent.Visibility = Visibility.Collapsed;
            UpdateCompactVisibility();
        };
        compactScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            scaleYAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ClearPrimaryEventAnimations()
    {
        EventAlertContent.BeginAnimation(OpacityProperty, null);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        EventAlertScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        EventAlertTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);

        EventCompactContent.BeginAnimation(OpacityProperty, null);
        EventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        EventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);

        MediaEventCompactContent.BeginAnimation(OpacityProperty, null);
        MediaEventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        MediaEventCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);

        CompactContent.BeginAnimation(OpacityProperty, null);
        CompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        CompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);

        MediaCompactContent.BeginAnimation(OpacityProperty, null);
        MediaCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        MediaCompactContentScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);

        EventAlertContent.Opacity = 1;
        EventAlertScale.ScaleX = 1;
        EventAlertScale.ScaleY = 1;
        EventAlertTranslate.Y = 0;
        EventCompactContent.Opacity = 1;
        EventCompactContentScale.ScaleX = 1;
        EventCompactContentScale.ScaleY = 1;
        MediaEventCompactContent.Opacity = 1;
        MediaEventCompactContentScale.ScaleX = 1;
        MediaEventCompactContentScale.ScaleY = 1;
        CompactContent.Opacity = 1;
        CompactContentScale.ScaleX = 1;
        CompactContentScale.ScaleY = 1;
        MediaCompactContent.Opacity = 1;
        MediaCompactContentScale.ScaleX = 1;
        MediaCompactContentScale.ScaleY = 1;
    }

    private void AnimateSecondaryEventContent(bool appearing)
    {
        SecondaryEventContent.BeginAnimation(OpacityProperty, null);
        SecondaryEventTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            null);
        if (!appearing || !ShouldAnimate)
        {
            SecondaryEventContent.Opacity = 1;
            SecondaryEventTranslate.X = 0;
            return;
        }

        SecondaryEventContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        SecondaryEventTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = new QuarticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
    }

    private static bool HasSameEventIdentity(
        CapsuleEvent? left,
        CapsuleEvent? right)
    {
        return left is null && right is null
               || left is not null
               && right is not null
               && string.Equals(
                   left.EventId,
                   right.EventId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLongRunningEvent(CapsuleEvent? capsuleEvent)
    {
        return capsuleEvent is not null
               && capsuleEvent.TaskState == CapsuleTaskState.Running
               && capsuleEvent.Kind is CapsuleEventKind.Timer
                   or CapsuleEventKind.Stopwatch
                   or CapsuleEventKind.TaskProgress;
    }

    private void ApplyCompactEvent(
        CapsuleEvent primaryEvent,
        CapsuleEvent? secondaryEvent)
    {
        PrimaryCompactProgressPath.Data = CreateProgressArcGeometry(
            GetCompactRingValue(primaryEvent),
            28,
            3);
        ApplyIndeterminateRingAnimation(
            PrimaryCompactProgressPath,
            IsIndeterminateDownload(primaryEvent));
        PrimaryCompactGlyphText.Text = GetCompactGlyph(primaryEvent);
        PrimaryCompactTimeText.Text = GetCompactStatusText(primaryEvent);
        PrimaryCompactTitleText.Text = primaryEvent.Title;
        ApplyMediaEventCompact(primaryEvent);

        var hasSecondary = IsLongRunningEvent(secondaryEvent);
        SecondaryCompactSeparator.Visibility = hasSecondary
            ? Visibility.Visible
            : Visibility.Collapsed;
        SecondaryCompactPanel.Visibility = hasSecondary
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!hasSecondary || secondaryEvent is null)
        {
            SecondaryCompactProgressPath.Data = Geometry.Empty;
            ApplyIndeterminateRingAnimation(
                SecondaryCompactProgressPath,
                false);
            return;
        }

        SecondaryCompactProgressPath.Data = CreateProgressArcGeometry(
            GetCompactRingValue(secondaryEvent),
            24,
            2.5);
        ApplyIndeterminateRingAnimation(
            SecondaryCompactProgressPath,
            IsIndeterminateDownload(secondaryEvent));
        SecondaryCompactGlyphText.Text = GetCompactGlyph(secondaryEvent);
        SecondaryCompactPanel.ToolTip =
            $"{secondaryEvent.Title} · {GetCompactStatusText(secondaryEvent)}";
    }

    private void ApplyMediaEventCompact(CapsuleEvent capsuleEvent)
    {
        if (_mediaSnapshot is not { } mediaSnapshot)
        {
            MediaEventCompactTitleText.Text = string.Empty;
            MediaEventCompactArtistText.Text = string.Empty;
            MediaEventCompactProgressPath.Data = Geometry.Empty;
            ApplyIndeterminateRingAnimation(
                MediaEventCompactProgressPath,
                false);
            return;
        }

        MediaEventCompactTitleText.Text = mediaSnapshot.Title;
        MediaEventCompactArtistText.Text = mediaSnapshot.IsPlaying
            ? mediaSnapshot.Artist
            : string.IsNullOrWhiteSpace(mediaSnapshot.Artist)
                ? "已暂停"
                : $"已暂停 · {mediaSnapshot.Artist}";
        MediaEventCompactProgressPath.Data = CreateProgressArcGeometry(
            GetCompactRingValue(capsuleEvent),
            24,
            2.5);
        ApplyIndeterminateRingAnimation(
            MediaEventCompactProgressPath,
            IsIndeterminateDownload(capsuleEvent));
        MediaEventCompactGlyphText.Text = GetCompactGlyph(capsuleEvent);
        MediaEventCompactContent.ToolTip =
            $"{mediaSnapshot.Title} · {capsuleEvent.Title} · "
            + GetCompactStatusText(capsuleEvent);
    }

    private static string GetCompactGlyph(CapsuleEvent capsuleEvent)
    {
        if (IsBrowserDownload(capsuleEvent))
        {
            return "\u2193";
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Stopwatch)
        {
            return capsuleEvent.Message.Contains(
                "已暂停",
                StringComparison.OrdinalIgnoreCase)
                ? "Ⅱ"
                : "◉";
        }

        if (capsuleEvent.Kind != CapsuleEventKind.Timer)
        {
            return "↗";
        }

        return capsuleEvent.Message.Contains(
            "已暂停",
            StringComparison.OrdinalIgnoreCase)
            ? "Ⅱ"
            : "◷";
    }

    private static string GetCompactStatusText(CapsuleEvent capsuleEvent)
    {
        if (IsBrowserDownload(capsuleEvent))
        {
            if (capsuleEvent.Progress is { } downloadProgress)
            {
                return $"{Math.Clamp(downloadProgress, 0, 1):P0}";
            }

            var separatorIndex = capsuleEvent.Message.IndexOf('·');
            return separatorIndex > 0
                ? capsuleEvent.Message[..separatorIndex].Trim()
                : "下载中";
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Stopwatch)
        {
            var separatorIndex = capsuleEvent.Message.LastIndexOf('·');
            if (separatorIndex >= 0)
            {
                return capsuleEvent.Message[(separatorIndex + 1)..].Trim();
            }

            const string elapsedMarker = "已计时 ";
            var elapsedIndex = capsuleEvent.Message.LastIndexOf(
                elapsedMarker,
                StringComparison.OrdinalIgnoreCase);
            return elapsedIndex >= 0
                ? capsuleEvent.Message[
                    (elapsedIndex + elapsedMarker.Length)..].Trim()
                : "00:00";
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Timer)
        {
            const string remainingMarker = "剩余 ";
            var markerIndex = capsuleEvent.Message.LastIndexOf(
                remainingMarker,
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return capsuleEvent.Message[
                    (markerIndex + remainingMarker.Length)..].Trim();
            }

            return capsuleEvent.Message.Contains(
                "已暂停",
                StringComparison.OrdinalIgnoreCase)
                ? "已暂停"
                : "计时中";
        }

        return capsuleEvent.Progress is { } progress
            ? $"{Math.Clamp(progress, 0, 1):P0}"
            : "进行中";
    }

    private static double GetCompactRingValue(CapsuleEvent capsuleEvent)
    {
        return Math.Clamp(
            capsuleEvent.Progress
            ?? (IsIndeterminateDownload(capsuleEvent) ? 0.28 : 0),
            0,
            1);
    }

    private static bool IsBrowserDownload(CapsuleEvent capsuleEvent)
    {
        return string.Equals(
            capsuleEvent.SourceId,
            BrowserDownloadProgressService.SourceId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIndeterminateDownload(CapsuleEvent capsuleEvent)
    {
        return IsBrowserDownload(capsuleEvent)
               && capsuleEvent.TaskState == CapsuleTaskState.Running
               && capsuleEvent.Progress is null
               && capsuleEvent.PrivacyLevel != EventPrivacyLevel.IconOnly;
    }

    private static void ApplyIndeterminateRingAnimation(
        System.Windows.Shapes.Path path,
        bool isIndeterminate)
    {
        path.RenderTransformOrigin = new Point(0.5, 0.5);
        if (path.RenderTransform is not RotateTransform rotateTransform)
        {
            rotateTransform = new RotateTransform();
            path.RenderTransform = rotateTransform;
        }

        if (!isIndeterminate)
        {
            rotateTransform.BeginAnimation(
                RotateTransform.AngleProperty,
                null);
            rotateTransform.Angle = 0;
            return;
        }

        if (rotateTransform.HasAnimatedProperties)
        {
            return;
        }

        rotateTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(
                0,
                360,
                TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private static Geometry CreateProgressArcGeometry(
        double value,
        double diameter,
        double strokeThickness)
    {
        value = Math.Clamp(value, 0, 1);
        if (value <= 0.001)
        {
            return Geometry.Empty;
        }

        var center = new Point(diameter / 2, diameter / 2);
        var radius = Math.Max(
            0,
            (diameter - strokeThickness) / 2);
        if (value >= 0.999)
        {
            var ellipse = new EllipseGeometry(center, radius, radius);
            ellipse.Freeze();
            return ellipse;
        }

        var angle = value * 360;
        var angleRadians = (angle - 90) * Math.PI / 180;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(
            center.X + radius * Math.Cos(angleRadians),
            center.Y + radius * Math.Sin(angleRadians));
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment(
            end,
            new System.Windows.Size(radius, radius),
            0,
            angle > 180,
            SweepDirection.Clockwise,
            true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void ApplyEventActions(CapsuleEvent capsuleEvent)
    {
        var isTimeEvent = capsuleEvent.Kind is CapsuleEventKind.Timer
            or CapsuleEventKind.Stopwatch;
        var isTerminalTimeEvent = isTimeEvent
                                  && capsuleEvent.TaskState
                                  != CapsuleTaskState.Running;
        var timerStatus = _countdownTimerService.CurrentStatus;
        var controlsLocalTimer =
            capsuleEvent.Kind == CapsuleEventKind.Timer
            && capsuleEvent.TaskState == CapsuleTaskState.Running
            && timerStatus.IsActive
            && string.Equals(
                capsuleEvent.EventId,
                timerStatus.EventId,
                StringComparison.OrdinalIgnoreCase);
        var windowsTimerState = new WindowsClockTimerControlState(
            false,
            false);
        var controlsWindowsTimer =
            capsuleEvent.Kind is CapsuleEventKind.Timer
                or CapsuleEventKind.Stopwatch
            && string.Equals(
                capsuleEvent.SourceId,
                WindowsClockTimerSyncService.SourceId,
                StringComparison.OrdinalIgnoreCase)
            && _windowsClockTimerService.TryGetControlState(
                capsuleEvent.EventId,
                out windowsTimerState);
        var stopwatchStatus = _stopwatchService.CurrentStatus;
        var controlsStopwatch =
            capsuleEvent.Kind == CapsuleEventKind.Stopwatch
            && capsuleEvent.TaskState == CapsuleTaskState.Running
            && !string.Equals(
                capsuleEvent.SourceId,
                WindowsClockTimerSyncService.SourceId,
                StringComparison.OrdinalIgnoreCase)
            && stopwatchStatus.IsActive
            && string.Equals(
                capsuleEvent.EventId,
                stopwatchStatus.EventId,
                StringComparison.OrdinalIgnoreCase);
        var controlsTimer = controlsLocalTimer || controlsWindowsTimer;
        var controlsTimeEvent = controlsTimer || controlsStopwatch;
        var showsTimerActions = controlsTimeEvent || isTerminalTimeEvent;

        EventActionPanel.Visibility = showsTimerActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        TimerPauseResumeButton.Visibility = isTerminalTimeEvent
            ? Visibility.Collapsed
            : Visibility.Visible;
        TimerCancelButton.Visibility = showsTimerActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        TimerPauseResumeButton.IsEnabled = controlsStopwatch
                                           || controlsLocalTimer
                                           || windowsTimerState.CanControl
                                           == true;
        TimerCancelButton.IsEnabled = isTerminalTimeEvent
                                      || TimerPauseResumeButton.IsEnabled;
        if (isTerminalTimeEvent)
        {
            TimerCancelButton.Content = "■";
            TimerCancelButton.ToolTip = controlsWindowsTimer
                ? "终止并重置 Windows 计时器"
                : capsuleEvent.Kind == CapsuleEventKind.Stopwatch
                    ? "关闭秒表结果"
                    : "终止计时";
            return;
        }

        TimerCancelButton.Content = controlsStopwatch
                                     || controlsWindowsTimer
                                     && capsuleEvent.Kind
                                     == CapsuleEventKind.Stopwatch
            ? "■"
            : "×";
        if (!controlsTimeEvent)
        {
            return;
        }

        var isPaused = controlsStopwatch
            ? stopwatchStatus.IsPaused
            : controlsWindowsTimer
                ? windowsTimerState.IsPaused
                : timerStatus.IsPaused;
        TimerPauseResumeButton.Content = isPaused
            ? "▶"
            : "Ⅱ";
        TimerPauseResumeButton.ToolTip = !TimerPauseResumeButton.IsEnabled
            ? "打开 Windows 时钟的计时器页面后可控制"
            : isPaused
                ? controlsStopwatch ? "继续秒表" : "继续计时"
                : controlsStopwatch ? "暂停秒表" : "暂停计时";
        TimerCancelButton.ToolTip = controlsStopwatch
            ? "终止秒表"
            : controlsWindowsTimer
            ? "重置 Windows 计时器"
            : "取消计时";
    }

    private void ApplySecondaryEvent(CapsuleEvent? capsuleEvent)
    {
        if (capsuleEvent is null)
        {
            SecondaryEventContent.Visibility = Visibility.Collapsed;
            EventAlertContent.Margin = new Thickness(0);
            return;
        }

        var (glyph, brush) = ResolveEventGlyph(capsuleEvent);
        SecondaryEventGlyphText.Text = glyph;
        SecondaryEventGlyphSurface.Background = brush;
        SecondaryEventTitleText.Text = string.IsNullOrWhiteSpace(
            capsuleEvent.Title)
            ? capsuleEvent.Source
            : capsuleEvent.Title;
        SecondaryEventStatusText.Text = capsuleEvent.Progress is { } progress
            ? $"{Math.Clamp(progress, 0, 1):P0}"
            : GetCompactStatusText(capsuleEvent);
        SecondaryEventContent.ToolTip =
            $"{capsuleEvent.Source} · {capsuleEvent.Title} · 单击切换到主任务";
        SecondaryEventContent.Visibility = Visibility.Visible;
        EventAlertContent.Margin = new Thickness(0, 0, 112, 0);
    }

    private void ApplyEventGlyph(CapsuleEvent capsuleEvent)
    {
        (EventGlyphText.Text, EventGlyphSurface.Background) =
            ResolveEventGlyph(capsuleEvent);
        StartEventGlyphAnimation(capsuleEvent);
    }

    private (string Glyph, Brush Brush) ResolveEventGlyph(
        CapsuleEvent capsuleEvent)
    {
        if (capsuleEvent.Kind == CapsuleEventKind.Notification)
        {
            return ("!", CreateEventBrush(42, 99, 245));
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Bluetooth)
        {
            return ("\uE702", CreateEventBrush(0, 120, 215));
        }

        if (capsuleEvent.Kind == CapsuleEventKind.WiFi)
        {
            return ("\uE701", CreateEventBrush(0, 153, 188));
        }

        if (IsBrowserDownload(capsuleEvent)
            && capsuleEvent.TaskState == CapsuleTaskState.Running)
        {
            return ("\u2193", CreateEventBrush(42, 99, 245));
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Timer
            && capsuleEvent.TaskState == CapsuleTaskState.Running)
        {
            return ("◷", CreateEventBrush(133, 91, 214));
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Stopwatch
            && capsuleEvent.TaskState == CapsuleTaskState.Running)
        {
            return ("◉", CreateEventBrush(27, 160, 133));
        }

        return capsuleEvent.TaskState switch
        {
            CapsuleTaskState.Succeeded =>
                ("✓", CreateEventBrush(37, 155, 94)),
            CapsuleTaskState.Failed =>
                ("!", CreateEventBrush(214, 69, 65)),
            CapsuleTaskState.Cancelled =>
                ("×", CreateEventBrush(99, 106, 118)),
            _ => ("↗", CreateEventBrush(42, 99, 245))
        };
    }

    private void StartEventGlyphAnimation(CapsuleEvent capsuleEvent)
    {
        StopEventGlyphAnimation();
        if (!ShouldAnimate)
        {
            return;
        }

        if (capsuleEvent.Kind == CapsuleEventKind.Bluetooth)
        {
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                CreateRepeatingGlyphAnimation(1, 1.1, 520));
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                CreateRepeatingGlyphAnimation(1, 1.1, 520));
            EventGlyphRotate.BeginAnimation(
                RotateTransform.AngleProperty,
                CreateRepeatingGlyphAnimation(-5, 5, 650));
            return;
        }

        if (capsuleEvent.Kind == CapsuleEventKind.WiFi)
        {
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                CreateRepeatingGlyphAnimation(0.94, 1.08, 700));
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                CreateRepeatingGlyphAnimation(0.94, 1.08, 700));
            EventGlyphText.BeginAnimation(
                OpacityProperty,
                CreateRepeatingGlyphAnimation(0.62, 1, 700));
            return;
        }

        if (IsIndeterminateDownload(capsuleEvent))
        {
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                CreateRepeatingGlyphAnimation(0.94, 1.07, 620));
            EventGlyphScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                CreateRepeatingGlyphAnimation(0.94, 1.07, 620));
        }
    }

    private static DoubleAnimation CreateRepeatingGlyphAnimation(
        double from,
        double to,
        int durationMilliseconds)
    {
        return new DoubleAnimation(
            from,
            to,
            TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
    }

    private void StopEventGlyphAnimation()
    {
        EventGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EventGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EventGlyphRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        EventGlyphText.BeginAnimation(OpacityProperty, null);
        EventGlyphScale.ScaleX = 1;
        EventGlyphScale.ScaleY = 1;
        EventGlyphRotate.Angle = 0;
        EventGlyphText.Opacity = 1;
    }

    private void ApplyEventProgress(CapsuleEvent capsuleEvent)
    {
        if (IsIndeterminateDownload(capsuleEvent))
        {
            EventProgressBar.IsIndeterminate = true;
            EventProgressBar.Visibility = Visibility.Visible;
            EventProgressText.Text = "下载中";
            EventProgressText.Visibility = Visibility.Visible;
            return;
        }

        EventProgressBar.IsIndeterminate = false;
        if (capsuleEvent.Kind is not (
                CapsuleEventKind.TaskProgress
                or CapsuleEventKind.Timer
                or CapsuleEventKind.Stopwatch)
            || capsuleEvent.Progress is null)
        {
            EventProgressBar.Visibility = Visibility.Collapsed;
            EventProgressText.Visibility = Visibility.Collapsed;
            return;
        }

        var progress = Math.Clamp(capsuleEvent.Progress.Value, 0, 1);
        EventProgressBar.Value = progress;
        EventProgressBar.Visibility = Visibility.Visible;
        EventProgressText.Text = $"{progress:P0}";
        EventProgressText.Visibility = Visibility.Visible;
    }

    private void ApplyEventPrivacyLayout(CapsuleEvent capsuleEvent)
    {
        var isIconOnly =
            capsuleEvent.PrivacyLevel == EventPrivacyLevel.IconOnly;
        EventTitleText.Visibility = isIconOnly
            ? Visibility.Collapsed
            : Visibility.Visible;
        EventDetailRow.Visibility = isIconOnly
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyPinnedPresentation(CapsuleEvent capsuleEvent)
    {
        var isPinned = _eventScheduler.IsPinned(capsuleEvent.EventId);
        PinnedIndicatorText.Visibility = isPinned
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActiveEventStatusChanged?.Invoke(true, isPinned);
    }

    private static string GetDefaultEventMessage(CapsuleEvent capsuleEvent)
    {
        return capsuleEvent.TaskState switch
        {
            CapsuleTaskState.Succeeded => "任务已完成",
            CapsuleTaskState.Failed => "任务失败",
            CapsuleTaskState.Cancelled => "任务已取消",
            CapsuleTaskState.Running => "正在执行",
            _ => string.Empty
        };
    }

    private SolidColorBrush CreateEventBrush(byte red, byte green, byte blue)
    {
        if (_accessibilityPreferences.HighContrast)
        {
            return System.Windows.SystemColors.HighlightBrush;
        }

        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void StopAnimationsAndApplyCurrentLayout()
    {
        _sizeAnimationVersion++;
        _presentationAnimationVersion++;
        StopSurfaceResizeAnimation();
        CapsuleSurface.HorizontalAlignment =
            System.Windows.HorizontalAlignment.Center;
        CapsuleSurface.VerticalAlignment =
            System.Windows.VerticalAlignment.Top;
        CapsuleSurface.Width = DesiredWidth;
        CapsuleSurface.Height = DesiredHeight;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        CapsuleSurfaceScale.ScaleX = 1;
        CapsuleSurfaceScale.ScaleY = 1;
        StopEventGlyphAnimation();
        ClearPrimaryEventAnimations();
        SecondaryEventContent.BeginAnimation(OpacityProperty, null);
        SecondaryEventTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            null);
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContentTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);

        _isAnimatingSize = false;
        Width = HostWidth;
        Height = HostHeight;
        SecondaryEventContent.Opacity = 1;
        SecondaryEventTranslate.X = 0;
        ExpandedContent.Opacity = 1;
        ExpandedContentTranslate.Y = 0;
        UpdateCompactVisibility();
        UpdateSurfacePresentation();
        UpdateTopStashPresentation(animate: false, DesiredHeight);
        PositionOnTargetMonitor();
    }

    private void ApplyMediaSnapshot(MediaSnapshot? snapshot)
    {
        if (snapshot is null
            || _pendingMediaSeekTrackIdentity is not null
               && !string.Equals(
                   _pendingMediaSeekTrackIdentity,
                   BuildMediaTrackIdentity(snapshot),
                   StringComparison.Ordinal))
        {
            ClearPendingMediaSeek();
        }

        _mediaSnapshot = snapshot;

        if (snapshot is null)
        {
            _isMediaExpandedOverEvent = false;
            ResetLyrics();
            MediaTitleText.Text = "未检测到媒体";
            MediaArtistText.Text = "播放音乐或视频后自动显示";
            LyricsText.Text = "♪ 歌词将在播放后显示";
            MediaCompactTitle.Text = string.Empty;
            MediaCompactLyricsText.Text = "♪ 歌词将在播放后显示";
            MediaCompactLyricsText.ToolTip = null;
            MediaCompactPlaybackIcon.Text = "▶";
            MediaEventCompactTitleText.Text = string.Empty;
            MediaEventCompactArtistText.Text = string.Empty;
            MediaEventCompactProgressPath.Data = Geometry.Empty;

            PreviousButton.IsEnabled = false;
            PlayPauseButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            _mediaProgressDurationSeconds = 1;
            _mediaScrubPositionSeconds = 0;
            UpdateMediaProgressVisual(TimeSpan.Zero, TimeSpan.Zero);
            MediaElapsedText.Text = "0:00";
            MediaRemainingText.Text = "−0:00";

            ApplyArtwork(null);
        }
        else
        {
            MediaCompactTitle.Text = snapshot.Title;
            MediaCompactPlaybackIcon.Text = snapshot.IsPlaying ? "Ⅱ" : "▶";

            MediaTitleText.Text = snapshot.Title;
            MediaArtistText.Text = snapshot.Artist;
            if (!ShouldPreservePendingMediaSeek(snapshot))
            {
                UpdateTimelineAnchor(snapshot);
            }
            EnsureLyrics(snapshot);

            PreviousButton.IsEnabled = snapshot.CanGoPrevious;
            PlayPauseButton.IsEnabled = snapshot.IsPlaying
                ? snapshot.CanPause
                : snapshot.CanPlay;
            NextButton.IsEnabled = snapshot.CanGoNext;
            PlayPauseButton.Content = snapshot.IsPlaying ? "Ⅱ" : "▶";

            ApplyArtwork(snapshot.Artwork ?? _lyricsResult?.Artwork);
            UpdateMediaProgress();
        }

        if (_activeCapsuleEvent is { } activeEvent
            && IsLongRunningEvent(activeEvent))
        {
            ApplyMediaEventCompact(activeEvent);
        }

        UpdateMediaStatusText();
        UpdateCompactVisibility();

        if ((_activeCapsuleEvent is null && !_isExpanded)
            || IsCompactActiveEvent)
        {
            AnimateWindowSize(DesiredWidth, DesiredHeight);
        }
    }

    private void EnsureLyrics(MediaSnapshot snapshot)
    {
        _lyricsTimer.Start();

        var identity = BuildLyricsTrackIdentity(snapshot);
        if (string.Equals(
                identity,
                _lyricsTrackIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            UpdateLyricsText();
            return;
        }

        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();

        _lyricsTrackIdentity = identity;
        _lyricsResult = null;
        _lyricsLookupCancellation = new CancellationTokenSource();
        LyricsText.Text = "♪ 正在查找歌词…";
        LyricsText.ToolTip = "正在查找同步歌词";
        MediaCompactLyricsText.Text = LyricsText.Text;
        MediaCompactLyricsText.ToolTip = LyricsText.ToolTip;

        _ = LoadLyricsAsync(
            snapshot,
            identity,
            _lyricsLookupCancellation.Token);
    }

    private async Task LoadLyricsAsync(
        MediaSnapshot snapshot,
        string identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _lyricsService.GetLyricsAsync(snapshot, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals(
                    identity,
                    _lyricsTrackIdentity,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lyricsResult = result;
            if (_mediaSnapshot?.Artwork is null && result.Artwork is not null)
            {
                ApplyArtwork(result.Artwork);
            }
            UpdateMediaProgress();
            UpdateLyricsText();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Changing tracks cancels the previous lookup.
        }
    }

    private void UpdateTimelineAnchor(MediaSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var position = snapshot.Position;
        var age = now - snapshot.TimelineUpdatedAt.ToUniversalTime();

        if (snapshot.IsPlaying
            && age >= TimeSpan.Zero
            && age < TimeSpan.FromHours(12))
        {
            position += age;
        }

        _timelineAnchorPosition = ClampPlaybackPosition(position, snapshot.EndTime);
        _timelineAnchorCapturedAt = now;
        _timelineIsPlaying = snapshot.IsPlaying;
    }

    private void OnLyricsTimerTick(object? sender, EventArgs e)
    {
        UpdateMediaProgress();
        if (_lyricsResult?.Status is LyricsLookupStatus.Found)
        {
            UpdateLyricsText();
        }
    }

    private void UpdateMediaProgress()
    {
        var snapshot = _mediaSnapshot;
        var duration = snapshot is null
            ? TimeSpan.Zero
            : GetEffectiveMediaDuration(snapshot);
        if (snapshot is null || duration <= TimeSpan.Zero)
        {
            _mediaProgressDurationSeconds = 1;
            _mediaScrubPositionSeconds = 0;
            MediaProgressHitTarget.Opacity = 0.45;
            MediaProgressHitTarget.ToolTip = "正在获取歌曲时长";
            UpdateMediaProgressVisual(TimeSpan.Zero, TimeSpan.Zero);
            MediaElapsedText.Text = "0:00";
            MediaRemainingText.Text = "−0:00";
            return;
        }

        MediaProgressHitTarget.Opacity = 1;
        if (_pendingMediaSeekPosition is null)
        {
            MediaProgressHitTarget.ToolTip = "点击或拖动以调整播放位置";
        }
        _mediaProgressDurationSeconds = Math.Max(
            1,
            duration.TotalSeconds);

        var position = _isMediaScrubbing
            ? TimeSpan.FromSeconds(_mediaScrubPositionSeconds)
            : GetEstimatedPlaybackPosition(duration);
        if (!_isMediaScrubbing)
        {
            _mediaScrubPositionSeconds = position.TotalSeconds;
        }

        UpdateMediaProgressVisual(position, duration);
        UpdateMediaProgressLabels(position, duration);
    }

    private void UpdateMediaProgressVisual(
        TimeSpan position,
        TimeSpan duration)
    {
        var width = MediaProgressHitTarget.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var ratio = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;
        var progressWidth = width * ratio;
        MediaProgressFill.Width = progressWidth;
        MediaProgressThumbTranslate.X = Math.Clamp(
            progressWidth - (MediaProgressThumb.Width / 2),
            -(MediaProgressThumb.Width / 2),
            width - (MediaProgressThumb.Width / 2));
    }

    private TimeSpan GetEffectiveMediaDuration(MediaSnapshot snapshot)
    {
        if (snapshot.EndTime > TimeSpan.Zero)
        {
            return snapshot.EndTime;
        }

        if (_lyricsResult?.Duration > TimeSpan.Zero)
        {
            return _lyricsResult.Duration;
        }

        var lastLyricTimestamp = _lyricsResult?.Lines.LastOrDefault()?.Timestamp
                                 ?? TimeSpan.Zero;
        return lastLyricTimestamp > TimeSpan.Zero
            ? lastLyricTimestamp + TimeSpan.FromSeconds(8)
            : TimeSpan.Zero;
    }

    private bool ShouldPreservePendingMediaSeek(MediaSnapshot snapshot)
    {
        if (_pendingMediaSeekPosition is not { } pendingPosition)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow >= _pendingMediaSeekUntil)
        {
            ClearPendingMediaSeek();
            return false;
        }

        if (Math.Abs((snapshot.Position - pendingPosition).TotalSeconds) <= 1.5)
        {
            ClearPendingMediaSeek();
            return false;
        }

        return true;
    }

    private void ClearPendingMediaSeek()
    {
        _pendingMediaSeekPosition = null;
        _pendingMediaSeekUntil = DateTimeOffset.MinValue;
        _pendingMediaSeekTrackIdentity = null;
    }

    private static string BuildMediaTrackIdentity(MediaSnapshot snapshot)
    {
        return string.Join(
            '\u001F',
            snapshot.SourceAppUserModelId,
            snapshot.Title,
            snapshot.Artist,
            snapshot.AlbumTitle);
    }

    private void UpdateMediaProgressLabels(
        TimeSpan position,
        TimeSpan endTime)
    {
        var clampedPosition = ClampPlaybackPosition(position, endTime);
        var remaining = endTime > clampedPosition
            ? endTime - clampedPosition
            : TimeSpan.Zero;
        MediaElapsedText.Text = FormatMediaDuration(clampedPosition);
        MediaRemainingText.Text = $"−{FormatMediaDuration(remaining)}";
    }

    private void UpdateLyricsText()
    {
        var snapshot = _mediaSnapshot;
        var result = _lyricsResult;
        if (snapshot is null)
        {
            return;
        }

        if (result is null)
        {
            LyricsText.Text = "♪ 正在查找歌词…";
            MediaCompactLyricsText.Text = LyricsText.Text;
            MediaCompactLyricsText.ToolTip = LyricsText.ToolTip;
            return;
        }

        switch (result.Status)
        {
            case LyricsLookupStatus.Found:
                var position = GetEstimatedPlaybackPosition(
                                   GetEffectiveMediaDuration(snapshot))
                               + TimeSpan.FromMilliseconds(150);
                var activeLine = FindActiveLyricLine(result.Lines, position);
                LyricsText.Text = activeLine?.Text ?? "♪ …";
                LyricsText.ToolTip = $"同步歌词来源：{result.Source}";
                break;

            case LyricsLookupStatus.Instrumental:
                LyricsText.Text = "♪ 纯音乐";
                LyricsText.ToolTip = $"歌词来源：{result.Source}";
                break;

            case LyricsLookupStatus.UnsyncedOnly:
                LyricsText.Text = GetFallbackLyricsText(snapshot, "♪ 仅找到非同步歌词");
                LyricsText.ToolTip = "当前歌词没有时间轴";
                break;

            case LyricsLookupStatus.NotFound:
                LyricsText.Text = GetFallbackLyricsText(snapshot, "♪ 暂无歌词");
                LyricsText.ToolTip = "未匹配到歌词";
                break;

            case LyricsLookupStatus.Unavailable:
                LyricsText.Text = GetFallbackLyricsText(snapshot, "♪ 歌词服务暂不可用");
                LyricsText.ToolTip = "网络不可用或歌词服务响应失败";
                break;
        }

        MediaCompactLyricsText.Text = LyricsText.Text;
        MediaCompactLyricsText.ToolTip = LyricsText.ToolTip;
    }

    private TimeSpan GetEstimatedPlaybackPosition(TimeSpan endTime)
    {
        var position = _timelineAnchorPosition;
        if (_timelineIsPlaying)
        {
            position += DateTimeOffset.UtcNow - _timelineAnchorCapturedAt;
        }

        return ClampPlaybackPosition(position, endTime);
    }

    private void ApplyArtwork(byte[]? artworkBytes)
    {
        if (_hasAppliedArtwork
            && ArtworkBytesMatch(_appliedArtworkBytes, artworkBytes))
        {
            return;
        }

        _hasAppliedArtwork = true;
        _appliedArtworkBytes = artworkBytes;
        var artwork = CreateArtwork(artworkBytes);
        ArtworkImage.Source = artwork;
        MediaCompactArtworkImage.Source = artwork;
        MediaEventCompactArtworkImage.Source = artwork;
        ArtworkImage.Visibility = artwork is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        MediaEventCompactArtworkImage.Visibility = artwork is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        MediaCompactArtworkImage.Visibility = artwork is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ArtworkFallback.Visibility = artwork is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaEventCompactArtworkFallback.Visibility = artwork is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaCompactArtworkFallback.Visibility = artwork is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool ArtworkBytesMatch(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
               && right is not null
               && left.Length == right.Length
               && left.AsSpan().SequenceEqual(right);
    }

    private void ResetLyrics()
    {
        _lyricsTimer.Stop();
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        _lyricsLookupCancellation = null;
        _lyricsTrackIdentity = null;
        _lyricsResult = null;
        _timelineAnchorPosition = TimeSpan.Zero;
        _timelineAnchorCapturedAt = DateTimeOffset.UtcNow;
        _timelineIsPlaying = false;
        LyricsText.ToolTip = null;
        MediaCompactLyricsText.ToolTip = null;
    }

    private static LyricLine? FindActiveLyricLine(
        IReadOnlyList<LyricLine> lines,
        TimeSpan position)
    {
        var low = 0;
        var high = lines.Count - 1;
        var activeIndex = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lines[middle].Timestamp <= position)
            {
                activeIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return activeIndex >= 0 ? lines[activeIndex] : null;
    }

    private static TimeSpan ClampPlaybackPosition(TimeSpan position, TimeSpan endTime)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return endTime > TimeSpan.Zero && position > endTime
            ? endTime
            : position;
    }

    private static string FormatMediaDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)Math.Floor(duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    private static string GetFallbackLyricsText(
        MediaSnapshot snapshot,
        string fallback)
    {
        return string.IsNullOrWhiteSpace(snapshot.LyricLine)
            ? fallback
            : snapshot.LyricLine;
    }

    private static string BuildLyricsTrackIdentity(MediaSnapshot snapshot)
    {
        return string.Join(
            '\u001F',
            snapshot.SourceAppUserModelId,
            snapshot.Title,
            snapshot.Artist,
            snapshot.AlbumTitle,
            Math.Round(snapshot.EndTime.TotalSeconds));
    }

    private void UpdateCompactVisibility()
    {
        if (_isQuickMenuOpen)
        {
            CompactContent.Visibility = Visibility.Collapsed;
            MediaCompactContent.Visibility = Visibility.Collapsed;
            EventCompactContent.Visibility = Visibility.Collapsed;
            MediaEventCompactContent.Visibility = Visibility.Collapsed;
            EventAlertContent.Visibility = Visibility.Collapsed;
            SecondaryEventContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Visibility = Visibility.Collapsed;
            PowerOffContent.Visibility = Visibility.Collapsed;
            QuickMenuContent.Visibility = Visibility.Visible;
            return;
        }

        QuickMenuContent.Visibility = Visibility.Collapsed;
        if (_isPaused)
        {
            CompactContent.Visibility = Visibility.Collapsed;
            MediaCompactContent.Visibility = Visibility.Collapsed;
            EventCompactContent.Visibility = Visibility.Collapsed;
            MediaEventCompactContent.Visibility = Visibility.Collapsed;
            EventAlertContent.Visibility = Visibility.Collapsed;
            SecondaryEventContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Visibility = Visibility.Collapsed;
            PowerOffContent.Visibility = Visibility.Visible;
            return;
        }

        PowerOffContent.Visibility = Visibility.Collapsed;
        if (_activeCapsuleEvent is not null)
        {
            if (_isMediaExpandedOverEvent)
            {
                CompactContent.Visibility = Visibility.Collapsed;
                MediaCompactContent.Visibility = Visibility.Collapsed;
                EventCompactContent.Visibility = Visibility.Collapsed;
                MediaEventCompactContent.Visibility = Visibility.Collapsed;
                EventAlertContent.Visibility = Visibility.Collapsed;
                SecondaryEventContent.Visibility = Visibility.Collapsed;
                ExpandedContent.Visibility = Visibility.Visible;
                return;
            }

            CompactContent.Visibility = Visibility.Collapsed;
            MediaCompactContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Visibility = Visibility.Collapsed;
            EventCompactContent.Visibility = IsCompactActiveEvent
                                             && !IsMediaEventCompact
                ? Visibility.Visible
                : Visibility.Collapsed;
            MediaEventCompactContent.Visibility = IsMediaEventCompact
                ? Visibility.Visible
                : Visibility.Collapsed;
            EventAlertContent.Visibility = IsCompactActiveEvent
                ? Visibility.Collapsed
                : Visibility.Visible;
            SecondaryEventContent.Visibility = IsCompactActiveEvent
                ? Visibility.Collapsed
                : _secondaryCapsuleEvent is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            return;
        }

        EventCompactContent.Visibility = Visibility.Collapsed;
        MediaEventCompactContent.Visibility = Visibility.Collapsed;
        EventAlertContent.Visibility = Visibility.Collapsed;
        SecondaryEventContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Visibility = _isExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isExpanded)
        {
            CompactContent.Visibility = Visibility.Collapsed;
            MediaCompactContent.Visibility = Visibility.Collapsed;
            return;
        }

        CompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaCompactContent.Visibility = _mediaSnapshot is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateSurfacePresentation()
    {
        var resourceKey = _isQuickMenuOpen
            ? "CapsuleSurfaceBrush"
            : !_isPaused
                          && ((_activeCapsuleEvent is not null
                               && !IsCompactActiveEvent)
                              || _isExpanded)
            ? "CapsuleSurfaceBrush"
            : "CompactCapsuleSurfaceBrush";
        CapsuleSurface.SetResourceReference(
            Border.BackgroundProperty,
            resourceKey);
    }

    private void UpdateMediaStatusText()
    {
        MediaSourceText.Text = _mediaSnapshot is null
            ? $"{_mediaStatus} · {_notificationStatus}"
            : $"{_mediaSnapshot.SourceDisplayName} · {_mediaStatus}";
    }

    private static ImageSource? CreateArtwork(byte[]? artworkBytes)
    {
        if (artworkBytes is null || artworkBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(artworkBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WmNcHitTest:
                var packedScreenCoordinates = lParam.ToInt64();
                var screenX = unchecked((short)(
                    packedScreenCoordinates & 0xFFFF));
                var screenY = unchecked((short)(
                    (packedScreenCoordinates >> 16) & 0xFFFF));
                var monitorInfo = new NativeMethods.MonitorInfo
                {
                    Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
                };
                if (_targetMonitor != nint.Zero
                    && NativeMethods.GetMonitorInfo(
                        _targetMonitor,
                        ref monitorInfo))
                {
                    var dpiScale = GetWindowScale();
                    var capsuleWidth = CapsuleSurface.ActualWidth > 0
                        ? CapsuleSurface.ActualWidth
                        : DesiredWidth;
                    var capsuleHeight = CapsuleSurface.ActualHeight > 0
                        ? CapsuleSurface.ActualHeight
                        : DesiredHeight;
                    var capsuleWidthPixels = capsuleWidth * dpiScale;
                    var capsuleHeightPixels = capsuleHeight * dpiScale;
                    var capsuleLeftPixels = monitorInfo.Work.Left
                        + ((monitorInfo.Work.Width - capsuleWidthPixels) / 2);
                    var capsuleTopPixels = monitorInfo.Work.Top
                        + ((GetEffectiveTopGap()
                            + CapsuleSurfaceTranslate.Y) * dpiScale);
                    var capsuleBoundsPixels = new Rect(
                        capsuleLeftPixels,
                        capsuleTopPixels,
                        capsuleWidthPixels,
                        capsuleHeightPixels);
                    if (!capsuleBoundsPixels.Contains(
                            new Point(screenX, screenY)))
                    {
                        handled = true;
                        return new nint(NativeMethods.HtTransparent);
                    }

                    handled = true;
                    return new nint(NativeMethods.HtClient);
                }

                break;

            case NativeMethods.WmMouseActivate:
                handled = true;
                return new nint(NativeMethods.MaNoActivate);

            case NativeMethods.WmRightButtonUp:
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    () => SetQuickMenuOpen(true));
                handled = true;
                return nint.Zero;

            case NativeMethods.WmDpiChanged:
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        PositionOnTargetMonitor();
                        ApplyRoundedWindowRegion();
                    });
                break;

            case NativeMethods.WmSettingChange:
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        PositionOnTargetMonitor();
                        ApplyRoundedWindowRegion();
                        _fullscreenDetector?.Refresh();
                    });
                break;

            case NativeMethods.WmDisplayChange:
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        _targetMonitor = ResolveInitialMonitor();
                        PositionOnTargetMonitor();
                        ApplyRoundedWindowRegion();
                        _fullscreenDetector?.Refresh();
                    });
                break;
        }

        return nint.Zero;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private static bool IsVisualDescendantOf(
        DependencyObject? element,
        DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor))
            {
                return true;
            }

            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private nint ResolveInitialMonitor()
    {
        if (_settings.MonitorTarget == MonitorTargetMode.PrimaryDisplay)
        {
            return NativeMethods.MonitorFromPoint(
                default,
                NativeMethods.MonitorDefaultToPrimary);
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow != nint.Zero && foregroundWindow != _windowHandle)
        {
            return NativeMethods.MonitorFromWindow(
                foregroundWindow,
                NativeMethods.MonitorDefaultToNearest);
        }

        return NativeMethods.GetCursorPos(out var cursorPosition)
            ? NativeMethods.MonitorFromPoint(cursorPosition, NativeMethods.MonitorDefaultToNearest)
            : NativeMethods.MonitorFromWindow(_windowHandle, NativeMethods.MonitorDefaultToNearest);
    }

    private void PositionOnTargetMonitor()
    {
        if (_windowHandle == nint.Zero || _targetMonitor == nint.Zero)
        {
            return;
        }

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(_targetMonitor, ref monitorInfo))
        {
            return;
        }

        const double logicalWidth = HostWidth;
        const double logicalHeight = HostHeight;
        var initialScale = GetWindowScale();
        SetWindowPosition(monitorInfo, initialScale, logicalWidth, logicalHeight);

        // Moving a Per-Monitor-V2 window can change its DPI. Re-read the DPI after
        // the first move and immediately correct the physical size if necessary.
        var targetScale = GetWindowScale();
        if (Math.Abs(targetScale - initialScale) > 0.001)
        {
            SetWindowPosition(monitorInfo, targetScale, logicalWidth, logicalHeight);
        }
    }

    private void RecenterAnimatedWindow()
    {
        if (_windowHandle == nint.Zero || _targetMonitor == nint.Zero)
        {
            return;
        }

        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfo(
                _targetMonitor,
                ref monitorInfo))
        {
            return;
        }

        var scale = GetWindowScale();
        var widthInPixels = Math.Max(
            1,
            (int)Math.Round(ActualWidth * scale));
        var gapInPixels = (int)Math.Round(
            GetEffectiveTopGap() * scale);
        var left = monitorInfo.Work.Left
                   + ((monitorInfo.Work.Width - widthInPixels) / 2);
        var top = monitorInfo.Work.Top + gapInPixels;
        NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTopmost,
            left,
            top,
            0,
            0,
            NativeMethods.SwpNoSize
            | NativeMethods.SwpNoActivate
            | (_isSuppressed ? 0 : NativeMethods.SwpShowWindow));
    }

    private void ApplyRoundedWindowRegion()
    {
        // The transparent host remains fixed at its maximum size. Applying a
        // Win32 region here would force DWM to rebuild the transparent window
        // during every morph and can expose a blank frame. The WPF surface owns
        // the rounded shape; WM_NCHITTEST makes the remaining host transparent
        // to pointer input.
    }

    private void ClearRoundedWindowRegion()
    {
        // Intentionally unused by the fixed-host animation path.
    }

    private void SetWindowPosition(
        NativeMethods.MonitorInfo monitorInfo,
        double dpiScale,
        double logicalWidth,
        double logicalHeight)
    {
        var widthInPixels = Math.Max(1, (int)Math.Round(logicalWidth * dpiScale));
        var heightInPixels = Math.Max(1, (int)Math.Round(logicalHeight * dpiScale));
        var gapInPixels = (int)Math.Round(GetEffectiveTopGap() * dpiScale);

        var left = monitorInfo.Work.Left + ((monitorInfo.Work.Width - widthInPixels) / 2);
        var top = monitorInfo.Work.Top + gapInPixels;

        NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTopmost,
            left,
            top,
            widthInPixels,
            heightInPixels,
            NativeMethods.SwpNoActivate
            | (_isSuppressed ? 0 : NativeMethods.SwpShowWindow));
    }

    private double GetEffectiveTopGap()
    {
        return 0;
    }

    private double GetWindowScale()
    {
        var dpi = NativeMethods.GetDpiForWindow(_windowHandle);
        return dpi > 0 ? dpi / 96d : 1d;
    }

    private static string FormatDiagnosticDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)Math.Ceiling(duration.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }
}
