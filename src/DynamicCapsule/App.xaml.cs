using DynamicCapsule.Models;
using DynamicCapsule.Services;
using System.Windows;

namespace DynamicCapsule;

public partial class App : System.Windows.Application
{
    private SettingsService? _settingsService;
    private StartupRegistrationService? _startupRegistrationService;
    private SingleInstanceService? _singleInstanceService;
    private AccessibilityPreferencesService? _accessibilityService;
    private TrayService? _trayService;
    private CapsuleWindow? _capsuleWindow;
    private SettingsWindow? _settingsWindow;
    private TimerWindow? _timerWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private AppSettings _settings = new();
    private AccessibilityPreferences _accessibilityPreferences =
        new(false, false);
    private StartupRegistrationStatus _startupStatus =
        new(false, true, "登录启动未启用");
    private NotificationListenerStatus? _lastNotificationStatus;
    private string? _lastNotificationStatusMessage;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.IsPrimaryInstance)
        {
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));
            var forwarded = _singleInstanceService.ForwardToPrimaryAsync(
                    e.Args,
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
            _singleInstanceService.Dispose();
            _singleInstanceService = null;
            Shutdown(forwarded ? 0 : 2);
            return;
        }

        _settingsService = new SettingsService();
        var loadResult = _settingsService.Load();
        _settings = loadResult.Settings;
        _accessibilityService = new AccessibilityPreferencesService();
        _accessibilityPreferences = _accessibilityService.Current;
        _accessibilityService.ApplyTheme(Resources);
        _accessibilityService.PreferencesChanged +=
            OnAccessibilityPreferencesChanged;
        _startupRegistrationService = new StartupRegistrationService();
        _startupStatus = _startupRegistrationService.GetStatus();
        if (_settings.StartWithWindows != _startupStatus.IsEnabled)
        {
            _settings = (_settings with
            {
                StartWithWindows = _startupStatus.IsEnabled
            }).Normalize();
        }

        _capsuleWindow = new CapsuleWindow(
            _settings,
            _accessibilityPreferences);
        _capsuleWindow.SettingsRequested += OnSettingsRequested;
        _capsuleWindow.PowerStateChangeRequested +=
            OnCapsulePowerStateChangeRequested;
        _capsuleWindow.DoNotDisturbChangeRequested +=
            OnDoNotDisturbChanged;
        _capsuleWindow.TopStashChangeRequested += OnTopStashChanged;
        _capsuleWindow.ExitRequested += OnExitRequested;
        _capsuleWindow.CountdownRequested += OnCountdownRequested;
        _capsuleWindow.CountdownStatusChanged += OnCountdownStatusChanged;
        _capsuleWindow.StopwatchStatusChanged += OnStopwatchStatusChanged;
        _capsuleWindow.ActiveEventStatusChanged += OnActiveEventStatusChanged;
        _capsuleWindow.NotificationStatusChanged +=
            OnNotificationStatusChanged;
        _capsuleWindow.Closed += OnCapsuleWindowClosed;
        MainWindow = _capsuleWindow;

        _trayService = new TrayService();
        _trayService.PauseChanged += OnPauseChanged;
        _trayService.CapsuleToggleRequested += OnCapsuleToggleRequested;
        _trayService.DoNotDisturbChanged += OnDoNotDisturbChanged;
        _trayService.CountdownRequested += OnCountdownRequested;
        _trayService.CountdownPauseResumeRequested +=
            OnCountdownPauseResumeRequested;
        _trayService.CountdownCancelRequested += OnCountdownCancelRequested;
        _trayService.StopwatchStartRequested += OnStopwatchStartRequested;
        _trayService.StopwatchPauseResumeRequested +=
            OnStopwatchPauseResumeRequested;
        _trayService.StopwatchStopRequested += OnStopwatchStopRequested;
        _trayService.ActiveEventPinToggleRequested +=
            OnActiveEventPinToggleRequested;
        _trayService.SettingsRequested += OnSettingsRequested;
        _trayService.DiagnosticsRequested += OnDiagnosticsRequested;
        _trayService.ExitRequested += OnExitRequested;
        _trayService.UpdatePaused(false);
        _trayService.UpdateDoNotDisturb(_settings.DoNotDisturb);

        _capsuleWindow.Show();
        _singleInstanceService.CommandReceived += OnSingleInstanceCommand;
        _singleInstanceService.StartListening();

        HandleLaunchArguments(e.Args, isForwarded: false);

        var startupWarning = _startupStatus.IsValid
            ? null
            : _startupStatus.Message;
        var startupMessage = string.Join(
            " · ",
            new[] { loadResult.Warning, startupWarning }
                .Where(message => !string.IsNullOrWhiteSpace(message)));
        if (!string.IsNullOrWhiteSpace(startupMessage))
        {
            Dispatcher.BeginInvoke(
                () => _trayService?.ShowStatus(
                    "Dynamic Capsule 设置",
                    startupMessage));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceService is not null)
        {
            _singleInstanceService.CommandReceived -= OnSingleInstanceCommand;
            _singleInstanceService.Dispose();
            _singleInstanceService = null;
        }

        if (_accessibilityService is not null)
        {
            _accessibilityService.PreferencesChanged -=
                OnAccessibilityPreferencesChanged;
            _accessibilityService.Dispose();
            _accessibilityService = null;
        }

        DisposeTrayService();
        base.OnExit(e);
    }

    private void OnAccessibilityPreferencesChanged(
        AccessibilityPreferences preferences)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnAccessibilityPreferencesChanged(preferences));
            return;
        }

        _accessibilityPreferences = preferences;
        _accessibilityService?.ApplyTheme(Resources);
        _capsuleWindow?.ApplyAccessibilityPreferences(preferences);
        _settingsWindow?.ApplyAccessibilityPreferences(preferences);
        _diagnosticsWindow?.UpdateAccessibility(preferences.Summary);
    }

    private void OnPauseChanged(bool isPaused)
    {
        OnCapsulePowerStateChangeRequested(isPaused);
    }

    private void OnCapsulePowerStateChangeRequested(bool isPoweredOff)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnCapsulePowerStateChangeRequested(isPoweredOff));
            return;
        }

        _capsuleWindow?.SetPaused(isPoweredOff);
        _trayService?.UpdatePaused(isPoweredOff);
    }

    private void OnCapsuleToggleRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnCapsuleToggleRequested);
            return;
        }

        if (_capsuleWindow?.ToggleExpandedFromAccessibility() == false)
        {
            _trayService?.ShowStatus(
                "Dynamic Capsule",
                "当前正在显示事件；可先固定或等待事件结束。");
        }
    }

    private async void OnSettingsRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)OnSettingsRequested);
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
            return;
        }

        if (_settingsService is null)
        {
            return;
        }

        var settingsWindow = new SettingsWindow(
            _settings,
            _settingsService.SettingsPath,
            _accessibilityPreferences,
            _lastNotificationStatus
                ?? NotificationListenerStatus.Unspecified,
            _lastNotificationStatusMessage
                ?? "正在检查通知能力");
        settingsWindow.NotificationPreviewRequested +=
            OnNotificationPreviewRequested;

        _settingsWindow = settingsWindow;
        var closed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => closed.TrySetResult(true);
        settingsWindow.Closed += closedHandler;

        try
        {
            settingsWindow.Show();
            settingsWindow.Activate();
            await closed.Task;

            if (settingsWindow.SavedSettings is not { } savedSettings)
            {
                return;
            }

            savedSettings = savedSettings.Normalize();
            var previousStartWithWindows = _startupStatus.IsEnabled;
            var startupRegistrationChanged =
                savedSettings.StartWithWindows != _startupStatus.IsEnabled
                || !_startupStatus.IsValid;
            if (startupRegistrationChanged)
            {
                if (_startupRegistrationService is null)
                {
                    System.Windows.MessageBox.Show(
                        "无法更新登录启动设置：服务不可用",
                        "Dynamic Capsule",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var startupUpdate =
                    await _startupRegistrationService.TrySetEnabledAsync(
                        savedSettings.StartWithWindows);
                if (!startupUpdate.Success)
                {
                    System.Windows.MessageBox.Show(
                        $"无法更新登录启动设置：{startupUpdate.Error}",
                        "Dynamic Capsule",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            if (!_settingsService.TrySave(savedSettings, out var error))
            {
                if (startupRegistrationChanged
                    && _startupRegistrationService is not null)
                {
                    await _startupRegistrationService.TrySetEnabledAsync(
                        previousStartWithWindows);
                }

                System.Windows.MessageBox.Show(
                    $"无法保存设置：{error}",
                    "Dynamic Capsule",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _startupStatus = _startupRegistrationService?.GetStatus()
                ?? new StartupRegistrationStatus(
                    false,
                    false,
                    "登录启动服务不可用");
            _settings = (savedSettings with
            {
                StartWithWindows = _startupStatus.IsEnabled
            }).Normalize();
            _capsuleWindow?.ApplySettings(_settings);
            _trayService?.UpdateDoNotDisturb(_settings.DoNotDisturb);
            _trayService?.ShowStatus("Dynamic Capsule", "设置已保存并应用。");
        }
        finally
        {
            settingsWindow.Closed -= closedHandler;
            settingsWindow.NotificationPreviewRequested -=
                OnNotificationPreviewRequested;
            _settingsWindow = null;
        }
    }

    private bool OnNotificationPreviewRequested(
        EventPrivacyLevel privacyLevel)
    {
        return _capsuleWindow?.ShowNotificationPreview(privacyLevel)
               == true;
    }

    private void OnExitRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnExitRequested);
            return;
        }

        RequestExit();
    }

    private void OnDiagnosticsRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnDiagnosticsRequested);
            return;
        }

        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Activate();
            return;
        }

        if (_capsuleWindow is null || _settingsService is null)
        {
            return;
        }

        _startupStatus = _startupRegistrationService?.GetStatus()
            ?? new StartupRegistrationStatus(
                false,
                false,
                "登录启动服务不可用");
        var snapshot = _capsuleWindow.CreateDiagnosticsSnapshot(
            _settingsService.SettingsPath,
            _startupStatus,
            _singleInstanceService?.IsPrimaryInstance == true);
        var diagnosticsWindow = new DiagnosticsWindow(snapshot);
        diagnosticsWindow.Closed += OnDiagnosticsWindowClosed;
        _diagnosticsWindow = diagnosticsWindow;
        diagnosticsWindow.Show();
    }

    private void OnDiagnosticsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is DiagnosticsWindow diagnosticsWindow)
        {
            diagnosticsWindow.Closed -= OnDiagnosticsWindowClosed;
        }

        _diagnosticsWindow = null;
    }

    private void OnSingleInstanceCommand(SingleInstanceCommand command)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnSingleInstanceCommand(command));
            return;
        }

        HandleLaunchArguments(command.Arguments, isForwarded: true);
    }

    private void HandleLaunchArguments(
        string[] arguments,
        bool isForwarded)
    {
        var countdown = TryGetStartupCountdown(arguments);
        if (countdown is not null)
        {
            _capsuleWindow?.StartCountdown(countdown.Value);
            if (isForwarded)
            {
                _trayService?.ShowStatus(
                    "Dynamic Capsule",
                    $"已接收计时命令：{countdown.Value.TotalMinutes:0} 分钟");
            }
        }

        var shouldShowSettings = arguments.Any(argument =>
            string.Equals(
                argument,
                "--show-settings",
                StringComparison.OrdinalIgnoreCase));
        var shouldShowDiagnostics = arguments.Any(argument =>
            string.Equals(
                argument,
                "--show-diagnostics",
                StringComparison.OrdinalIgnoreCase));
        var shouldToggleCapsule = arguments.Any(argument =>
            string.Equals(
                argument,
                "--toggle-capsule",
                StringComparison.OrdinalIgnoreCase));
        var shouldToggleCountdown = arguments.Any(argument =>
            string.Equals(
                argument,
                "--toggle-countdown",
                StringComparison.OrdinalIgnoreCase));
        var shouldResetCountdown = arguments.Any(argument =>
            string.Equals(
                argument,
                "--reset-countdown",
                StringComparison.OrdinalIgnoreCase));
        var shouldExit = arguments.Any(argument =>
            string.Equals(
                argument,
                "--exit",
                StringComparison.OrdinalIgnoreCase));
        if (shouldExit)
        {
            RequestExit();
            return;
        }

        if (shouldToggleCapsule)
        {
            _capsuleWindow?.ToggleExpandedFromAccessibility();
        }

        if (shouldToggleCountdown)
        {
            _capsuleWindow?.ToggleCountdownPause();
        }

        if (shouldResetCountdown)
        {
            _capsuleWindow?.CancelCountdown();
        }

        if (shouldShowDiagnostics)
        {
            OnDiagnosticsRequested();
        }

        if (shouldShowSettings
            || (isForwarded
                && countdown is null
                && !shouldShowDiagnostics
                && !shouldToggleCapsule
                && !shouldToggleCountdown
                && !shouldResetCountdown
                && !shouldExit))
        {
            OnSettingsRequested();
        }
    }

    private void OnCountdownRequested(int? minutes)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnCountdownRequested(minutes));
            return;
        }

        if (minutes is { } presetMinutes)
        {
            _capsuleWindow?.StartCountdown(
                TimeSpan.FromMinutes(presetMinutes));
            return;
        }

        if (_timerWindow is not null)
        {
            _timerWindow.Activate();
            return;
        }

        var timerWindow = new TimerWindow(_accessibilityPreferences);

        _timerWindow = timerWindow;
        try
        {
            if (timerWindow.ShowDialog() == true)
            {
                _capsuleWindow?.StartCountdown(
                    timerWindow.Duration,
                    timerWindow.TimerTitle);
            }
        }
        finally
        {
            _timerWindow = null;
        }
    }

    private void OnCountdownPauseResumeRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnCountdownPauseResumeRequested);
            return;
        }

        _capsuleWindow?.ToggleCountdownPause();
    }

    private void OnCountdownCancelRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnCountdownCancelRequested);
            return;
        }

        _capsuleWindow?.CancelCountdown();
    }

    private void OnStopwatchStartRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnStopwatchStartRequested);
            return;
        }

        _capsuleWindow?.StartStopwatch();
    }

    private void OnStopwatchPauseResumeRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnStopwatchPauseResumeRequested);
            return;
        }

        _capsuleWindow?.ToggleStopwatchPause();
    }

    private void OnStopwatchStopRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnStopwatchStopRequested);
            return;
        }

        _capsuleWindow?.StopStopwatch();
    }

    private void OnActiveEventPinToggleRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnActiveEventPinToggleRequested);
            return;
        }

        _capsuleWindow?.ToggleActiveEventPinned();
    }

    private void OnCountdownStatusChanged(CountdownTimerStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnCountdownStatusChanged(status));
            return;
        }

        _trayService?.UpdateCountdown(status);
    }

    private void OnStopwatchStatusChanged(StopwatchStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStopwatchStatusChanged(status));
            return;
        }

        _trayService?.UpdateStopwatch(status);
    }

    private void OnNotificationStatusChanged(
        NotificationListenerStatus status,
        string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnNotificationStatusChanged(status, message));
            return;
        }

        if (_lastNotificationStatus == status
            && string.Equals(
                _lastNotificationStatusMessage,
                message,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastNotificationStatus = status;
        _lastNotificationStatusMessage = message;
        _settingsWindow?.UpdateNotificationStatus(status, message);
        if (status is NotificationListenerStatus.Allowed)
        {
            return;
        }

        var guidance = status switch
        {
            NotificationListenerStatus.PackageIdentityRequired =>
                "当前运行的是未打包开发版，微信等 Windows 通知需要安装 "
                + "Microsoft Store/MSIX 版并授权后才能读取。",
            NotificationListenerStatus.Denied =>
                "通知读取权限未获授权，请在 Windows 设置中允许后重新启动。",
            NotificationListenerStatus.Unspecified =>
                "尚未取得通知读取权限，请重新启动并完成系统授权。",
            _ => message
        };
        _trayService?.ShowStatus("Windows 通知未连接", guidance);
    }

    private void OnActiveEventStatusChanged(
        bool hasActiveEvent,
        bool isPinned)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => OnActiveEventStatusChanged(
                    hasActiveEvent,
                    isPinned));
            return;
        }

        _trayService?.UpdateActiveEvent(hasActiveEvent, isPinned);
    }

    private void OnDoNotDisturbChanged(bool isEnabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnDoNotDisturbChanged(isEnabled));
            return;
        }

        if (_settingsService is null)
        {
            return;
        }

        var previousSettings = _settings;
        var nextSettings = (_settings with
        {
            DoNotDisturb = isEnabled
        }).Normalize();

        if (!_settingsService.TrySave(nextSettings, out var error))
        {
            _trayService?.UpdateDoNotDisturb(previousSettings.DoNotDisturb);
            _trayService?.ShowStatus(
                "Dynamic Capsule",
                $"无法保存勿扰设置：{error}");
            return;
        }

        _settings = nextSettings;
        _capsuleWindow?.ApplySettings(_settings);
        _settingsWindow?.UpdateDoNotDisturb(_settings.DoNotDisturb);
        _trayService?.UpdateDoNotDisturb(_settings.DoNotDisturb);
    }

    private void OnTopStashChanged(bool isEnabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTopStashChanged(isEnabled));
            return;
        }

        if (_settingsService is null)
        {
            return;
        }

        var nextSettings = (_settings with
        {
            TopStashed = isEnabled
        }).Normalize();

        if (!_settingsService.TrySave(nextSettings, out var error))
        {
            _trayService?.ShowStatus(
                "Dynamic Capsule",
                $"无法保存顶部收纳设置：{error}");
            return;
        }

        _settings = nextSettings;
        _capsuleWindow?.ApplySettings(_settings);
    }

    private void OnCapsuleWindowClosed(object? sender, EventArgs e)
    {
        if (sender is CapsuleWindow capsuleWindow)
        {
            capsuleWindow.SettingsRequested -= OnSettingsRequested;
            capsuleWindow.PowerStateChangeRequested -=
                OnCapsulePowerStateChangeRequested;
            capsuleWindow.DoNotDisturbChangeRequested -=
                OnDoNotDisturbChanged;
            capsuleWindow.TopStashChangeRequested -= OnTopStashChanged;
            capsuleWindow.ExitRequested -= OnExitRequested;
            capsuleWindow.CountdownStatusChanged -= OnCountdownStatusChanged;
            capsuleWindow.ActiveEventStatusChanged -= OnActiveEventStatusChanged;
            capsuleWindow.NotificationStatusChanged -=
                OnNotificationStatusChanged;
            capsuleWindow.Closed -= OnCapsuleWindowClosed;
        }

        _capsuleWindow = null;
        if (!_isExiting)
        {
            RequestExit();
        }
    }

    private void RequestExit()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _settingsWindow?.Close();
        _settingsWindow = null;
        _timerWindow?.Close();
        _timerWindow = null;
        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Closed -= OnDiagnosticsWindowClosed;
            _diagnosticsWindow.Close();
            _diagnosticsWindow = null;
        }

        DisposeTrayService();

        if (_capsuleWindow is not null)
        {
            _capsuleWindow.SettingsRequested -= OnSettingsRequested;
            _capsuleWindow.PowerStateChangeRequested -=
                OnCapsulePowerStateChangeRequested;
            _capsuleWindow.DoNotDisturbChangeRequested -=
                OnDoNotDisturbChanged;
            _capsuleWindow.TopStashChangeRequested -= OnTopStashChanged;
            _capsuleWindow.ExitRequested -= OnExitRequested;
            _capsuleWindow.CountdownRequested -= OnCountdownRequested;
            _capsuleWindow.CountdownStatusChanged -= OnCountdownStatusChanged;
            _capsuleWindow.StopwatchStatusChanged -= OnStopwatchStatusChanged;
            _capsuleWindow.ActiveEventStatusChanged -= OnActiveEventStatusChanged;
            _capsuleWindow.NotificationStatusChanged -=
                OnNotificationStatusChanged;
            _capsuleWindow.Closed -= OnCapsuleWindowClosed;
            _capsuleWindow.Close();
            _capsuleWindow = null;
        }

        Shutdown();
    }

    private void DisposeTrayService()
    {
        if (_trayService is null)
        {
            return;
        }

        _trayService.PauseChanged -= OnPauseChanged;
        _trayService.CapsuleToggleRequested -= OnCapsuleToggleRequested;
        _trayService.DoNotDisturbChanged -= OnDoNotDisturbChanged;
        _trayService.CountdownRequested -= OnCountdownRequested;
        _trayService.CountdownPauseResumeRequested -=
            OnCountdownPauseResumeRequested;
        _trayService.CountdownCancelRequested -= OnCountdownCancelRequested;
        _trayService.StopwatchStartRequested -= OnStopwatchStartRequested;
        _trayService.StopwatchPauseResumeRequested -=
            OnStopwatchPauseResumeRequested;
        _trayService.StopwatchStopRequested -= OnStopwatchStopRequested;
        _trayService.ActiveEventPinToggleRequested -=
            OnActiveEventPinToggleRequested;
        _trayService.SettingsRequested -= OnSettingsRequested;
        _trayService.DiagnosticsRequested -= OnDiagnosticsRequested;
        _trayService.ExitRequested -= OnExitRequested;
        _trayService.Dispose();
        _trayService = null;
    }

    private static TimeSpan? TryGetStartupCountdown(string[] arguments)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    "--timer-minutes",
                    StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(arguments[index + 1], out var minutes)
                || minutes is < 1 or > 1440)
            {
                continue;
            }

            return TimeSpan.FromMinutes(minutes);
        }

        return null;
    }
}
