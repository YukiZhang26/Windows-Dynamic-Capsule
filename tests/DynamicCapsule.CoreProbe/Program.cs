using DynamicCapsule.Models;
using DynamicCapsule.Services;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Automation;

if (args.Contains("--video-progress"))
{
    if (!MediaSessionService.IsVideoPlayback(Windows.Media.MediaPlaybackType.Video, "browser")
        || !MediaSessionService.IsVideoPlayback(null, "com.bilibili.bilibiliPC")
        || MediaSessionService.IsVideoPlayback(Windows.Media.MediaPlaybackType.Music, "com.bilibili.bilibiliPC")
        || MediaSessionService.IsVideoPlayback(null, "browser"))
        throw new Exception("Incorrect stash media classification");
    Console.WriteLine("Video, music, Bilibili fallback and unknown browser cases passed.");
    return 0;
}

if (args.Contains("--ai-browser-live"))
{
    foreach (var name in new[] { "msedge", "chrome", "firefox" })
    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
    using (p)
    {
        if (p.MainWindowHandle == IntPtr.Zero) continue;
        var s = AiActivityMonitor.Observe(p.MainWindowHandle, name);
        Console.WriteLine($"{name}: source={s?.Source ?? "none"}, running={s?.Stop == true}");
    }
    return 0;
}

if (args.Contains("--ai-activity"))
{
    var t = new AiActivityMonitor.Tracker();
    var now = DateTimeOffset.UtcNow;
    var a = new AiActivityMonitor.Signal("a", "ChatGPT", true, 1, 0);
    var b = new AiActivityMonitor.Signal("b", "豆包", true, 0, 0);
    var c = new AiActivityMonitor.Signal("c", "ChatGPT Web", true, 0, 0);
    if (t.Step([a with { Stop = false }], now).Length != 0) throw new Exception("Idle app created a task");
    if (t.Step([a,b,c], now).Count(x => x.State == "Running") != 3) throw new Exception("Missing simultaneous tasks");
    var result = t.Step([a with { Stop = false, Copies = 2 }, b with { Stop = false, Errors = 1 }], now);
    if (result.Length != 2 || !result.Any(x => x.State == "Completed") || !result.Any(x => x.State == "Error"))
        throw new Exception("Terminal or missing-window transition incorrect");
    if (t.Step([], now.AddSeconds(7)).Length != 0) throw new Exception("Expired rows remained");
    t.Step([a], now);
    t.Step([a with { Stop = false }], now);
    if (t.Step([a with { Stop = false }], now.AddSeconds(4)).Length != 0) throw new Exception("Unknown outcome reported");
    Console.WriteLine("AI activity probe passed: idle, three simultaneous tasks, completion, error, lost window and expiry.");
    return 0;
}

if (args.Contains("--audio-spectrum"))
{
    var analyzer = new SpectrumAnalyzer();
    var samples = new float[4096];
    for (var i = 0; i < 2048; i++)
    {
        samples[2 * i] = (float)(0.4 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
        samples[2 * i + 1] = -samples[2 * i]; // Stereo anti-phase must not cancel.
    }
    var bands = analyzer.Push(samples)!;
    var peak = Array.IndexOf(bands, bands.Max());
    if (peak is < 7 or > 9 || bands.Max() < 0.5) throw new Exception("FFT frequency/anti-phase check failed.");
    if (analyzer.Push(new float[4096])!.Any(value => value != 0)) throw new Exception("Silence check failed.");
    using var capture = new AudioSpectrumService();
    await capture.SetSourceAsync(args.Contains("--bilibili") ? "com.bilibili.bilibiliPC" : "QQMusic.exe");
    var max = 0f;
    for (var i = 0; i < 60; i++) { await Task.Delay(100); max = Math.Max(max, capture.ReadBands().Max()); }
    Console.WriteLine($"FFT and silence passed. Live status: {capture.Status}; peak={max:F3}");
    await capture.SetSourceAsync(null);
    if (capture.ReadBands().Any(value => value != 0)) throw new Exception("Stop did not clear spectrum.");
    return max > 0 ? 0 : 2;
}

if (args.Contains("--settings-render"))
{
    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            var app = new DynamicCapsule.App();
            app.InitializeComponent();
            var panel = new DynamicCapsule.SettingsWindow(new AppSettings(), "本地设置",
                new AccessibilityPreferences(false, false), NotificationListenerStatus.Allowed,
                "通知权限已开启。勿扰仅屏蔽消息提醒。");
            var content = (System.Windows.FrameworkElement)panel.Content;
            var host = new System.Windows.Controls.Border
            {
                Background = (System.Windows.Media.Brush)app.Resources["WindowSurfaceBrush"],
                Child = null
            };
            panel.Content = null;
            host.Resources = panel.Resources;
            host.Child = content;
            host.Measure(new System.Windows.Size(760, 650));
            host.Arrange(new System.Windows.Rect(0, 0, 760, 650));
            host.UpdateLayout();
            panel.EnableAnimationsCheckBox.IsChecked = true;
            panel.HideInFullscreenCheckBox.IsChecked = false;
            var knob = (System.Windows.FrameworkElement)panel.HideInFullscreenCheckBox.Template.FindName("Knob", panel.HideInFullscreenCheckBox);
            var knobMotion = (System.Windows.Media.TranslateTransform)knob.RenderTransform;
            if (!knobMotion.HasAnimatedProperties) throw new Exception("Switch transition did not start.");
            panel.ApplyAccessibilityPreferences(new AccessibilityPreferences(false, true));
            if (knobMotion.HasAnimatedProperties || knobMotion.X != 0) throw new Exception("Reduced motion did not stop switch animation.");
            panel.HideInFullscreenCheckBox.IsChecked = true;
            if (knobMotion.X != 18) throw new Exception("Reduced-motion switch has an incorrect resting position.");
            panel.ApplyAccessibilityPreferences(new AccessibilityPreferences(false, false));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(760, 650, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/ui");
            using (var file = File.Create("artifacts/ui/control-panel.png")) encoder.Save(file);
            for (var tab = 1; tab < 3; tab++)
            {
                panel.SettingsTabs.SelectedIndex = tab;
                host.UpdateLayout();
                var tabBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(760, 650, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                tabBitmap.Render(host);
                var tabEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                tabEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(tabBitmap));
                using var tabFile = File.Create($"artifacts/ui/control-panel-{tab}.png");
                tabEncoder.Save(tabFile);
            }
            panel.Close();
            app.Shutdown();
        }
        catch (Exception ex) { renderFailure = ex; }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();
    renderThread.Join();
    if (renderFailure is not null) { Console.Error.WriteLine(renderFailure); return 1; }
    Console.WriteLine("Settings panel rendered.");
    return 0;
}

if (args.Any(argument => string.Equals(
        argument,
        "--live-clock",
        StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        return await ProbeWindowsClockLiveAsync();
    }
    catch (Exception exception)
    {
        try
        {
            TryPauseAndResetClockActivity(
                ["TimerPlayPauseButton"],
                ["TimerResetButton"]);
            TryPauseAndResetClockActivity(
                [
                    "StopwatchPlayPauseButton",
                    "StopWatchPlayPauseButton"
                ],
                ["StopwatchResetButton", "StopWatchResetButton"]);
        }
        catch
        {
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            succeeded = false,
            error = exception.ToString()
        }));
        return 1;
    }
}

if (args.Any(argument => string.Equals(
        argument,
        "--quick-menu",
        StringComparison.OrdinalIgnoreCase)))
{
    return ProbeCapsuleQuickMenu();
}

if (args.Any(argument => string.Equals(
        argument,
        "--media-layout",
        StringComparison.OrdinalIgnoreCase)))
{
    return ProbeExpandedMediaLayout();
}

if (args.Any(argument => string.Equals(
        argument,
        "--media-session",
        StringComparison.OrdinalIgnoreCase)))
{
    return await ProbeMediaSessionAsync();
}

if (args.Any(argument => string.Equals(
        argument,
        "--qqmusic-lyrics",
        StringComparison.OrdinalIgnoreCase)))
{
    return await ProbeQqMusicLyricsAsync();
}

var failures = new List<string>();
var policy = new EventPolicyEngine();
var notification = CreateEvent(CapsuleEventKind.Notification, "app.id", "邮件");
var task = CreateEvent(CapsuleEventKind.TaskProgress, "codex", "Codex");
var quietSettings = new AppSettings { DoNotDisturb = true }.Normalize();
Assert(!policy.Evaluate(notification, quietSettings).IsAllowed,
    "do not disturb must block message notifications");
foreach (var kind in Enum.GetValues<CapsuleEventKind>()
             .Where(kind => kind != CapsuleEventKind.Notification))
{
    Assert(policy.Evaluate(CreateEvent(kind, "system", "activity"), quietSettings).IsAllowed,
        $"do not disturb must preserve {kind}");
}
using (var quietScheduler = new CapsuleEventScheduler())
{
    quietScheduler.Publish(task);
    quietScheduler.Publish(notification);
    quietScheduler.ApplyPolicy(e => policy.Evaluate(e, quietSettings).CapsuleEvent);
    Assert(quietScheduler.ActiveEvent?.EventId == task.EventId,
        "enabling do not disturb must remove notifications and retain tasks");
    quietScheduler.ApplyPolicy(e => policy.Evaluate(e, new AppSettings()).CapsuleEvent);
    Assert(quietScheduler.ActiveEvent?.EventId == task.EventId && quietScheduler.SecondaryEvent is null,
        "disabling do not disturb must not replay suppressed notifications");
}
var parsedLyrics = LyricsService.ParseSyncedLyrics(
    "[00:02.29]第一行\n[01:04.48]第二行");
Assert(
    parsedLyrics.Count == 2
    && parsedLyrics[0].Timestamp == TimeSpan.FromSeconds(2.29)
    && parsedLyrics[1].Timestamp == TimeSpan.FromSeconds(64.48),
    "synchronized lyrics must preserve minute and fractional-second timestamps");

Assert(
    policy.Evaluate(
        notification,
        new AppSettings
        {
            NotificationBlockList = ["app.id"]
        }.Normalize()).Reason == EventPolicyReason.BlockedSource,
    "block list must reject a matching notification");

Assert(
    policy.Evaluate(
        notification,
        new AppSettings
        {
            NotificationAllowList = ["another.app"]
        }.Normalize()).Reason == EventPolicyReason.SourceNotAllowed,
    "non-empty allow list must reject an unmatched notification");

Assert(
    policy.Evaluate(
        notification,
        new AppSettings
        {
            NotificationAllowList = ["邮件"]
        }.Normalize()).IsAllowed,
    "allow list must match the visible source name");

Assert(
    policy.Evaluate(
        task,
        new AppSettings
        {
            NotificationBlockList = ["codex"]
        }.Normalize()).IsAllowed,
    "notification source rules must not block local tasks");

var summary = policy.Evaluate(
    notification,
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.Summary
    }.Normalize()).CapsuleEvent;
Assert(
    summary is not null
    && summary.Message.Length == 72
    && summary.Message.EndsWith('…'),
    "summary privacy must truncate long event text");

var masked = policy.Evaluate(
    notification,
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.Masked
    }.Normalize()).CapsuleEvent;
Assert(
    masked is not null
    && masked.Title == "通知内容已隐藏"
    && masked.Message == "内容已隐藏",
    "masked privacy must replace title and body");

var iconOnly = policy.Evaluate(
    task,
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.IconOnly
    }.Normalize()).CapsuleEvent;
Assert(
    iconOnly is not null
    && iconOnly.Title.Length == 0
    && iconOnly.Message.Length == 0
    && iconOnly.Progress is null,
    "icon-only privacy must remove task details and progress");

var maskedTimer = policy.Evaluate(
    CreateEvent(CapsuleEventKind.Timer, "timer", "计时器"),
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.Masked
    }.Normalize()).CapsuleEvent;
Assert(
    maskedTimer?.Title == "计时内容已隐藏",
    "masked privacy must use timer-specific text");

var maskedBluetooth = policy.Evaluate(
    CreateEvent(CapsuleEventKind.Bluetooth, "system.bluetooth", "蓝牙"),
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.Masked
    }.Normalize()).CapsuleEvent;
Assert(
    maskedBluetooth?.Title == "蓝牙状态已隐藏",
    "masked privacy must use Bluetooth-specific text");

var maskedWiFi = policy.Evaluate(
    CreateEvent(CapsuleEventKind.WiFi, "system.wifi", "Wi-Fi"),
    new AppSettings
    {
        PrivacyLevel = EventPrivacyLevel.Masked
    }.Normalize()).CapsuleEvent;
Assert(
    maskedWiFi?.Title == "网络状态已隐藏",
    "masked privacy must use Wi-Fi-specific text");

VerifyPinnedPriority();
VerifyPrimarySecondaryPresentation();
VerifyBatchMerge();
VerifySettingsFallback();
if (AudioSpectrumService.ResolveProcessName("com.bilibili.bilibiliPC") != "哔哩哔哩"
    || AudioSpectrumService.ResolveProcessName("QQMusic.exe") != "QQMusic"
    || AudioSpectrumService.ResolveProcessName("unknown.bilibiliPC") is not null)
    throw new Exception("Audio process identity mapping regression.");
{
    double slow = 0, fast = 0, slowVelocity = 0, fastVelocity = 0;
    for (var i = 0; i < 30; i++) DynamicCapsule.SpectrumView.Smooth(ref slow, ref slowVelocity, 1, 1.0 / 30);
    for (var i = 0; i < 144; i++) DynamicCapsule.SpectrumView.Smooth(ref fast, ref fastVelocity, 1, 1.0 / 144);
    if (Math.Abs(slow - fast) > 0.0001 || slow < 0.99 || slow > 1)
        throw new Exception("Spectrum smoothing is not frame-rate independent.");
    for (var i = 0; i < 120; i++) DynamicCapsule.SpectrumView.Smooth(ref fast, ref fastVelocity, 0, 1.0 / 120);
    if (fast > 0.001 || fast < 0) throw new Exception("Spectrum release failed to settle.");
}
if (MediaSessionService.ScoreSession("Games.exe", "index.html#?lang=zh-cn", "", true, true) != -1
    || MediaSessionService.ScoreSession("QQMusic.exe", "Track", "Artist", true, false)
       <= MediaSessionService.ScoreSession("Games.exe", "index.html", "", true, true)
    || MediaSessionService.ScoreSession("QQMusic.exe", "Track", "Artist", false, false)
       >= MediaSessionService.ScoreSession("msedge.exe", "Video title", "", true, true)
    || MediaSessionService.ScoreSession("msedge.exe", "Video title", "", true, true) < 0)
    throw new InvalidOperationException("Media session priority regression.");
if (new AppSettings { BackgroundTransparency = -10 }.Normalize().BackgroundTransparency != 0
    || new AppSettings { BackgroundTransparency = 200 }.Normalize().BackgroundTransparency != 80
    || new AppSettings { BackgroundTransparency = double.NaN }.Normalize().BackgroundTransparency != 20)
    throw new InvalidOperationException("Background transparency normalization failed.");
await VerifyPersistentLyricsCacheAsync();
await VerifyLyricsRecoveryAsync();
VerifyWindowsClockTimerParsing();
VerifyWindowsClockStatusText();
VerifyWindowsClockLaunchTarget();
VerifyQqMusicSeekCommands();
VerifyBrowserDownloadProgress();

if (failures.Count > 0)
{
    Console.Error.WriteLine("Core probe failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine(
    "Core probe passed: source rules, privacy, batch scheduling, settings, persistent lyrics cache, and Windows Clock parsing/status.");
return 0;

int ProbeCapsuleQuickMenu()
{
    Exception? failure = null;
    var rendered = false;
    var thread = new Thread(() =>
    {
        DynamicCapsule.App? application = null;
        DynamicCapsule.CapsuleWindow? window = null;
        try
        {
            application = new DynamicCapsule.App();
            application.InitializeComponent();
            window = new DynamicCapsule.CapsuleWindow(
                new AppSettings().Normalize(),
                new AccessibilityPreferences(
                    HighContrast: false,
                    ReduceMotion: true));
            window.CapsuleSurface.Width = 504;
            foreach (var digits in new[] { window.MediaEventCompactTimeText, window.SecondaryCompactTimeText })
            {
                if (digits.Visibility != System.Windows.Visibility.Visible || digits.FontSize >= window.PrimaryCompactTimeText.FontSize)
                    throw new InvalidOperationException("Secondary time must remain visible and smaller than primary time.");
                digits.Text = "23:59:59";
                digits.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                if (digits.DesiredSize.Width > 82)
                    throw new InvalidOperationException("Secondary time exceeds its compact lane.");
            }
            if (((System.Windows.Media.SolidColorBrush)window.CapsuleSurface.Background).Color.A != 204)
                throw new InvalidOperationException("Initial background transparency was not applied.");
            window.ApplySettings(new AppSettings { BackgroundTransparency = 60 });
            if (((System.Windows.Media.SolidColorBrush)window.CapsuleSurface.Background).Color.A != 102)
                throw new InvalidOperationException("Saved background transparency was not applied.");
            window.CapsuleSurface.Height = 120;
            window.QuickMenuContent.Visibility =
                System.Windows.Visibility.Visible;
            window.QuickMenuContent.Opacity = 1;
            window.CapsuleSurface.Measure(
                new System.Windows.Size(504, 120));
            window.CapsuleSurface.Arrange(
                new System.Windows.Rect(0, 0, 504, 120));
            window.CapsuleSurface.UpdateLayout();
            rendered = window.QuickMenuContent.Visibility
                       == System.Windows.Visibility.Visible
                       && window.QuickMenuContent.ActualWidth > 0
                       && window.QuickToggleButton.ActualWidth > 0;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            window?.Close();
            application?.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        Console.Error.WriteLine(
            $"Quick menu probe failed: {failure}");
        return 1;
    }

    if (!rendered)
    {
        Console.Error.WriteLine(
            "Quick menu probe failed: expanded content did not render.");
        return 1;
    }

    Console.WriteLine(
        "Quick menu probe passed: commands rendered inside the expanded capsule.");
    return 0;
}

int ProbeExpandedMediaLayout()
{
    Exception? failure = null;
    var rendered = false;
    var thread = new Thread(() =>
    {
        DynamicCapsule.App? application = null;
        DynamicCapsule.CapsuleWindow? window = null;
        try
        {
            application = new DynamicCapsule.App();
            application.InitializeComponent();
            window = new DynamicCapsule.CapsuleWindow(
                new AppSettings().Normalize(),
                new AccessibilityPreferences(
                    HighContrast: false,
                    ReduceMotion: true));
            typeof(DynamicCapsule.CapsuleWindow).GetField("_isExpanded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
            window.ApplySettings(new AppSettings().Normalize());
            window.CapsuleSurface.Width = 470;
            if (window.TopStashArrowButton.Visibility != System.Windows.Visibility.Collapsed ||
                window.MediaTopStashArrowButton.Visibility != System.Windows.Visibility.Collapsed)
                throw new Exception("Expanded music must not show a side collapse button.");
            window.CapsuleSurface.Height = 238;
            window.ExpandedContent.Visibility =
                System.Windows.Visibility.Visible;
            window.ExpandedContent.Opacity = 1;
            window.CapsuleSurface.Measure(
                new System.Windows.Size(470, 238));
            window.CapsuleSurface.Arrange(
                new System.Windows.Rect(0, 0, 470, 238));
            window.CapsuleSurface.UpdateLayout();

            var playButtonOrigin = window.PlayPauseButton.TranslatePoint(
                new System.Windows.Point(),
                window.CapsuleSurface);
            var spectrumOrigin = window.ExpandedSpectrum.TranslatePoint(new System.Windows.Point(), window.MediaProgressHitTarget);
            var previousCenter = window.PreviousButton.TranslatePoint(new System.Windows.Point(window.PreviousButton.ActualWidth / 2, 0), window.CapsuleSurface).X;
            var playCenter = window.PlayPauseButton.TranslatePoint(new System.Windows.Point(window.PlayPauseButton.ActualWidth / 2, 0), window.CapsuleSurface).X;
            var nextCenter = window.NextButton.TranslatePoint(new System.Windows.Point(window.NextButton.ActualWidth / 2, 0), window.CapsuleSurface).X;
            if (Math.Abs(playCenter - window.CapsuleSurface.ActualWidth / 2) > 0.5
                || Math.Abs((playCenter - previousCenter) - (nextCenter - playCenter)) > 0.5)
                throw new Exception("Playback controls are not centered and symmetrical.");
            if (window.ExpandedSpectrum.IsHitTestVisible
                || Math.Abs(window.ExpandedSpectrum.ActualWidth - window.MediaProgressHitTarget.ActualWidth) > 0.5
                || Math.Abs(spectrumOrigin.Y + window.ExpandedSpectrum.ActualHeight - (window.MediaProgressHitTarget.ActualHeight / 2 - 2)) > 0.5)
                throw new Exception("Spectrum must sit directly above the timeline without intercepting seeking.");
            rendered = window.ExpandedContent.ActualHeight > 190
                       && window.MediaProgressHitTarget.ActualWidth > 250
                       && window.MediaElapsedText.ActualWidth > 0
                       && window.MediaRemainingText.ActualWidth > 0
                       && window.PreviousButton.ActualWidth > 0
                       && window.PlayPauseButton.ActualWidth > 0
                       && window.NextButton.ActualWidth > 0
                       && playButtonOrigin.Y >= 0
                       && playButtonOrigin.Y
                          + window.PlayPauseButton.ActualHeight <= 238;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = window.GetType();
            type.GetField("_mediaSnapshot", flags)!.SetValue(window, new MediaSnapshot("player", "Player", "Song", "Artist", "", null, true, true, true, true, true,
                TimeSpan.Zero, TimeSpan.FromMinutes(3), DateTimeOffset.UtcNow, null));
            var ai = new CapsuleEvent("ai:activity-stack", CapsuleEventKind.AiActivity, CapsuleEventPriority.Normal, "ai-monitor", "AI", "Codex", "Running",
                null, CapsuleTaskState.Running, EventPrivacyLevel.Full, DateTimeOffset.UtcNow, null);
            void Show(CapsuleEvent? item) => type.GetMethod("ApplyPresentedCapsuleEvents",flags)!.Invoke(window,[item,null]);
            Show(ai);
            if (window.ExpandedContent.Visibility != System.Windows.Visibility.Visible) throw new Exception("AI stole expanded media");
            Show(null);
            if (window.ExpandedContent.Visibility != System.Windows.Visibility.Visible) throw new Exception("AI expiry collapsed media");
            type.GetField("_isExpanded", flags)!.SetValue(window,false);
            Show(ai);
            Show(ai with { TaskState = CapsuleTaskState.Succeeded, Message = "Completed" });
            if (window.MediaEventCompactContent.Visibility != System.Windows.Visibility.Visible || window.MediaEventCompactTimeText.Text != "Completed"
                || window.MediaEventCompactTitleText.Text != "Song") throw new Exception("AI displaced compact media");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            window?.Close();
            application?.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        Console.Error.WriteLine(
            $"Expanded media layout probe failed: {failure}");
        return 1;
    }

    if (!rendered)
    {
        Console.Error.WriteLine(
            "Expanded media layout probe failed: timeline or controls were clipped.");
        return 1;
    }

    Console.WriteLine(
        "Expanded media layout probe passed: timeline and controls fit inside the capsule.");
    return 0;
}

void VerifyWindowsClockTimerParsing()
{
    Assert(
        WindowsClockTimerParser.TryParseDuration(
            "2 分 59 秒",
            out var chineseDuration)
        && chineseDuration == TimeSpan.FromSeconds(179),
        "Windows Clock parser must read Chinese minute/second values");
    Assert(
        WindowsClockTimerParser.TryParseDuration(
            "01:02:03",
            out var colonDuration)
        && colonDuration == TimeSpan.FromSeconds(3723),
        "Windows Clock parser must read colon-formatted durations");
    Assert(
        WindowsClockTimerParser.TryParseDuration(
            "−00:00:21",
            out var overdueDuration)
        && overdueDuration == TimeSpan.Zero,
        "Windows Clock parser must treat overdue negative values as complete");
    Assert(
        WindowsClockTimerParser.TryParseDuration(
            "1 hour 4 minutes 8 seconds",
            out var englishDuration)
        && englishDuration == TimeSpan.FromSeconds(3848),
        "Windows Clock parser must read English unit durations");
    Assert(
        WindowsClockTimerParser.ParseRunState(
            "编辑计时器，1 分钟，已暂停，55 秒",
            "计时器已暂停，开始")
        == WindowsClockTimerRunState.Paused,
        "Windows Clock parser must identify paused timers");
    Assert(
        WindowsClockTimerParser.ParseRunState(
            "编辑计时器，3 分钟，正在运行",
            "暂停计时器")
        == WindowsClockTimerRunState.Running,
        "Windows Clock parser must identify running timers");
    Assert(
        WindowsClockTimerParser.ParseRunState(
            "编辑计时器，5 分钟，未开始",
            "开始计时器")
        == WindowsClockTimerRunState.NotStarted,
        "Windows Clock parser must ignore timers that have not started");
    Assert(
        WindowsClockTimerParser.CreateEventId("3 分钟", 0)
        == WindowsClockTimerParser.CreateEventId("  3 分钟 ", 0)
        && WindowsClockTimerParser.CreateEventId("3 分钟", 0)
        != WindowsClockTimerParser.CreateEventId("3 分钟", 1),
        "Windows Clock event IDs must be stable and distinguish duplicate cards");
}

void VerifyWindowsClockStatusText()
{
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: false,
            timerPageAvailable: false,
            timerCount: 0,
            stopwatchPageAvailable: false,
            stopwatchCount: 0)
        == "等待 Windows 时钟（打开“时钟 > 计时器或秒表”后同步）",
        "Windows Clock idle status must mention both timers and stopwatches");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: false,
            timerPageAvailable: false,
            timerCount: 1,
            stopwatchPageAvailable: false,
            stopwatchCount: 1)
        == "Windows 时钟不可见 · 本地镜像 1 个计时器、秒表",
        "Windows Clock hidden status must describe every mirrored activity");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: true,
            timerPageAvailable: false,
            timerCount: 0,
            stopwatchPageAvailable: false,
            stopwatchCount: 0)
        == "已检测到 Windows 时钟 · 计时器/秒表页面暂不可读取",
        "a Clock splash or inaccessible page must not be reported as absent");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: true,
            timerPageAvailable: false,
            timerCount: 1,
            stopwatchPageAvailable: false,
            stopwatchCount: 1)
        == "Windows 时钟页面暂不可读取 · 本地镜像 1 个计时器、秒表",
        "an inaccessible Clock page must preserve mirrored activities");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: true,
            timerPageAvailable: true,
            timerCount: 0,
            stopwatchPageAvailable: false,
            stopwatchCount: 0)
        == "已连接 Windows 时钟 · 暂无运行中的计时器或秒表",
        "an open Clock page without activity must report a connected idle state");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: true,
            timerPageAvailable: false,
            timerCount: 0,
            stopwatchPageAvailable: true,
            stopwatchCount: 1)
        == "已连接 Windows 时钟 · 同步秒表",
        "Windows Clock stopwatch-only status must report synchronized stopwatch");
    Assert(
        WindowsClockTimerSyncService.BuildStatus(
            clockWindowAvailable: true,
            timerPageAvailable: true,
            timerCount: 2,
            stopwatchPageAvailable: false,
            stopwatchCount: 1)
        == "已连接 Windows 时钟 · 同步 2 个计时器 · 本地镜像秒表",
        "Windows Clock mixed status must distinguish synchronized and mirrored activity");
}

void VerifyWindowsClockLaunchTarget()
{
    Assert(
        WindowsClockTimerSyncService.ClockAppUserModelId
        == "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"
        && WindowsClockTimerSyncService.ClockShellTarget
        == "shell:AppsFolder\\Microsoft.WindowsAlarms_8wekyb3d8bbwe!App",
        "Windows Clock must use the package AUMID shell target before the legacy protocol fallback");
}

async Task<int> ProbeWindowsClockLiveAsync()
{
    var gate = new object();
    var synchronizedEvents = new Dictionary<string, CapsuleEvent>(
        StringComparer.OrdinalIgnoreCase);
    using var service = new WindowsClockTimerSyncService();
    service.EventsChanged += (changedEvents, removedIds) =>
    {
        lock (gate)
        {
            foreach (var removedId in removedIds)
            {
                synchronizedEvents.Remove(removedId);
            }

            foreach (var changedEvent in changedEvents)
            {
                synchronizedEvents[changedEvent.EventId] = changedEvent;
            }
        }
    };
    service.Start();

    var timerPageOpened = await service.OpenTimerAsync();
    var timerStarted = timerPageOpened
                       && await TryStartClockActivityAsync(
                           TryStartClockTimer);
    var timerEvent = timerStarted
        ? await WaitForClockEventAsync(CapsuleEventKind.Timer)
        : null;
    var timerControlAvailable = timerEvent is not null
                                && service.TryGetControlState(
                                    timerEvent.EventId,
                                    out var timerControl)
                                && timerControl.CanControl
                                && !timerControl.IsPaused;
    var timerPaused = timerEvent is not null
                      && await service.TogglePauseAsync(timerEvent.EventId);
    if (timerPaused)
    {
        await Task.Delay(800);
    }

    var timerReset = timerEvent is not null
                     && await service.ResetAsync(timerEvent.EventId);
    await Task.Delay(800);

    var stopwatchPageOpened = await service.OpenStopwatchAsync();
    var stopwatchStarted = stopwatchPageOpened
                           && await TryStartClockActivityAsync(
                               TryStartClockStopwatch);
    var stopwatchEvent = stopwatchStarted
        ? await WaitForClockEventAsync(CapsuleEventKind.Stopwatch)
        : null;
    if (stopwatchEvent is not null)
    {
        await Task.Delay(1200);
    }

    var stopwatchControlAvailable = stopwatchEvent is not null
                                    && service.TryGetControlState(
                                        stopwatchEvent.EventId,
                                        out var stopwatchControl)
                                    && stopwatchControl.CanControl
                                    && !stopwatchControl.IsPaused;
    var stopwatchPaused = stopwatchEvent is not null
                          && await service.TogglePauseAsync(
                              stopwatchEvent.EventId);
    if (stopwatchPaused)
    {
        await Task.Delay(800);
    }

    var stopwatchReset = stopwatchEvent is not null
                         && await service.ResetAsync(
                             stopwatchEvent.EventId);
    await Task.Delay(800);

    TryPauseAndResetClockActivity(
        ["TimerPlayPauseButton"],
        ["TimerResetButton"]);
    TryPauseAndResetClockActivity(
        ["StopwatchPlayPauseButton", "StopWatchPlayPauseButton"],
        ["StopwatchResetButton", "StopWatchResetButton"]);

    var succeeded = timerPageOpened
                    && timerStarted
                    && timerEvent is not null
                    && timerControlAvailable
                    && timerPaused
                    && timerReset
                    && stopwatchPageOpened
                    && stopwatchStarted
                    && stopwatchEvent is not null
                    && stopwatchControlAvailable
                    && stopwatchPaused
                    && stopwatchReset;
    var result = new
    {
        succeeded,
        serviceStatus = service.Status,
        timer = new
        {
            pageOpened = timerPageOpened,
            started = timerStarted,
            eventObserved = timerEvent,
            controlAvailable = timerControlAvailable,
            paused = timerPaused,
            reset = timerReset
        },
        stopwatch = new
        {
            pageOpened = stopwatchPageOpened,
            started = stopwatchStarted,
            eventObserved = stopwatchEvent,
            controlAvailable = stopwatchControlAvailable,
            paused = stopwatchPaused,
            reset = stopwatchReset
        }
    };
    Console.WriteLine(JsonSerializer.Serialize(
        result,
        new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    return succeeded ? 0 : 1;

    async Task<CapsuleEvent?> WaitForClockEventAsync(
        CapsuleEventKind kind)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            lock (gate)
            {
                var current = synchronizedEvents.Values
                    .Where(candidate => candidate.Kind == kind)
                    .OrderByDescending(candidate => candidate.CreatedAt)
                    .FirstOrDefault();
                if (current is not null)
                {
                    return current;
                }
            }

            await Task.Delay(250);
        }

        return null;
    }

    async Task<bool> TryStartClockActivityAsync(Func<bool> tryStart)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (tryStart())
            {
                return true;
            }

            await Task.Delay(150);
        }

        return false;
    }
}

bool TryStartClockTimer()
{
    var root = FindClockRootForLiveProbe();
    var timerPage = FindClockElementByAutomationId(
        root,
        "TimerScrollViewer");
    if (timerPage is null)
    {
        return false;
    }

    var cards = timerPage.FindAll(
        TreeScope.Descendants,
        new PropertyCondition(
            AutomationElement.AutomationIdProperty,
            "TimerViewGrid"));
    foreach (AutomationElement card in cards)
    {
        var playPauseButton = FindClockElementByAutomationId(
            card,
            "TimerPlayPauseButton");
        var actionName = GetClockElementName(playPauseButton);
        if (ContainsClockProbeText(actionName, "Start", "开始")
            && TryInvokeClockElement(playPauseButton))
        {
            return true;
        }
    }

    return false;
}

bool TryStartClockStopwatch()
{
    var root = FindClockRootForLiveProbe();
    var playPauseButton = FindClockElementByAutomationIds(
        root,
        "StopwatchPlayPauseButton",
        "StopWatchPlayPauseButton");
    return ContainsClockProbeText(
               GetClockElementName(playPauseButton),
               "Start",
               "开始")
           && TryInvokeClockElement(playPauseButton);
}

void TryPauseAndResetClockActivity(
    IReadOnlyList<string> playPauseAutomationIds,
    IReadOnlyList<string> resetAutomationIds)
{
    var root = FindClockRootForLiveProbe();
    var playPauseButton = FindClockElementByAutomationIds(
        root,
        [.. playPauseAutomationIds]);
    if (ContainsClockProbeText(
            GetClockElementName(playPauseButton),
            "Pause",
            "暂停"))
    {
        TryInvokeClockElement(playPauseButton);
        Thread.Sleep(350);
    }

    var resetButton = FindClockElementByAutomationIds(
        root,
        [.. resetAutomationIds]);
    TryInvokeClockElement(resetButton);
}

AutomationElement? FindClockRootForLiveProbe()
{
    try
    {
        var desktopChildren = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            Condition.TrueCondition);
        foreach (AutomationElement candidate in desktopChildren)
        {
            var name = GetClockElementName(candidate);
            if (string.Equals(
                    name,
                    "Clock",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    name,
                    "Alarms & Clock",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    name,
                    "时钟",
                    StringComparison.OrdinalIgnoreCase)
                || FindClockElementByAutomationId(
                    candidate,
                    "TimerButton") is not null)
            {
                return candidate;
            }
        }
    }
    catch (ElementNotAvailableException)
    {
    }

    return null;
}

AutomationElement? FindClockElementByAutomationId(
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

AutomationElement? FindClockElementByAutomationIds(
    AutomationElement? root,
    params string[] automationIds)
{
    foreach (var automationId in automationIds)
    {
        var element = FindClockElementByAutomationId(root, automationId);
        if (element is not null)
        {
            return element;
        }
    }

    return null;
}

bool TryInvokeClockElement(AutomationElement? element)
{
    if (element is null)
    {
        return false;
    }

    try
    {
        if (element.TryGetCurrentPattern(
                InvokePattern.Pattern,
                out var invokePattern))
        {
            ((InvokePattern)invokePattern).Invoke();
            return true;
        }

        if (element.TryGetCurrentPattern(
                SelectionItemPattern.Pattern,
                out var selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            return true;
        }
    }
    catch (Exception exception) when (
        exception is ElementNotAvailableException
        or InvalidOperationException)
    {
    }

    return false;
}

string GetClockElementName(AutomationElement? element)
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

bool ContainsClockProbeText(
    string value,
    params string[] candidates)
{
    return candidates.Any(candidate => value.Contains(
        candidate,
        StringComparison.OrdinalIgnoreCase));
}

void VerifySettingsFallback()
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"DynamicCapsule.CoreProbe.{Guid.NewGuid():N}");
    var settingsService = new SettingsService(temporaryDirectory);

    try
    {
        var saved = settingsService.TrySave(
            new AppSettings
            {
                TopGap = 100,
                TopStashed = true,
                PrivacyLevel = EventPrivacyLevel.Masked,
                LyricsFallbackProvider = LyricsFallbackProvider.None,
                NotificationBlockList = [" app.id ", "APP.ID"]
            },
            out var saveError);
        Assert(saved, $"settings save must succeed: {saveError}");

        var loaded = settingsService.Load();
        Assert(
            !loaded.UsedDefaults
            && loaded.Settings.TopGap == AppSettings.MaximumTopGap
            && loaded.Settings.TopStashed
            && loaded.Settings.PrivacyLevel == EventPrivacyLevel.Masked
            && loaded.Settings.LyricsFallbackProvider
                == LyricsFallbackProvider.None
            && loaded.Settings.NotificationBlockList.SequenceEqual(["app.id"]),
            "saved settings must normalize and round-trip");

        File.WriteAllText(
            settingsService.SettingsPath,
            """
            {
              "schemaVersion": 3,
              "monitorTarget": "followActiveWindow",
              "topGap": 10,
              "enableAnimations": true,
              "hideInFullscreen": true,
              "doNotDisturb": false,
              "startWithWindows": false,
              "privacyLevel": "summary",
              "notificationAllowList": [],
              "notificationBlockList": []
            }
            """);
        var legacy = settingsService.Load();
        Assert(
            !legacy.UsedDefaults
            && legacy.Settings.LyricsFallbackProvider
                == LyricsFallbackProvider.QqMusic,
            "existing schema 3 settings must gain the default lyrics fallback without reset");

        File.WriteAllText(settingsService.SettingsPath, "{ invalid json");
        var corrupt = settingsService.Load();
        Assert(
            corrupt.UsedDefaults
            && corrupt.Settings.SchemaVersion == AppSettings.CurrentSchemaVersion
            && corrupt.Settings.TopGap == AppSettings.DefaultTopGap
            && corrupt.Settings.PrivacyLevel == EventPrivacyLevel.Summary
            && !string.IsNullOrWhiteSpace(corrupt.Warning),
            "corrupt settings must fall back to defaults with a warning");
    }
    finally
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

async Task VerifyLyricsRecoveryAsync()
{
    var cachePath = Path.Combine(Path.GetTempPath(), $"capsule-recovery-{Guid.NewGuid():N}");
    var snapshot = new MediaSnapshot("test", "test", "Recovery", "Artist", "Album", null,
        true, true, true, true, true, TimeSpan.Zero, TimeSpan.FromSeconds(180), DateTimeOffset.UtcNow, null);
    var recovered = false;
    var handler = new ProbeHttpMessageHandler(request =>
    {
        if (!recovered) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        var record = """{"trackName":"Recovery","artistName":"Artist","duration":180,"syncedLyrics":"[00:01.00]test"}""";
        var exact = request.RequestUri!.AbsolutePath == "/api/get";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(exact ? record : $"[{record}]")
        };
    });
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.net") };
    using var service = new LyricsService(client, cachePath);
    service.UpdateFallbackProvider(LyricsFallbackProvider.None);
    Assert((await service.GetLyricsAsync(snapshot, CancellationToken.None)).Status == LyricsLookupStatus.Unavailable,
        "HTTP failures must be reported as unavailable");
    recovered = true;
    Assert((await service.GetLyricsAsync(snapshot, CancellationToken.None)).Status == LyricsLookupStatus.Found,
        "same track must recover after a transient network failure");
    if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
}

async Task VerifyPersistentLyricsCacheAsync()
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"dynamic-capsule-lyrics-{Guid.NewGuid():N}");
    var snapshot = new MediaSnapshot(
        "probe.player",
        "Probe Player",
        "Cache Song",
        "Cache Artist",
        "Cache Album",
        null,
        true,
        true,
        true,
        true,
        true,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(180),
        DateTimeOffset.UtcNow,
        null);

    try
    {
        var onlineHandler = new ProbeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": 1,
                      "trackName": "Cache Song",
                      "artistName": "Cache Artist",
                      "albumName": "Cache Album",
                      "duration": 180,
                      "instrumental": false,
                      "plainLyrics": "cached line",
                      "syncedLyrics": "[00:01.00]cached line"
                    }
                    """)
            });
        using (var onlineClient = new HttpClient(onlineHandler)
               {
                   BaseAddress = new Uri("https://lrclib.net")
               })
        using (var onlineService = new LyricsService(
                   onlineClient,
                   temporaryDirectory))
        {
            onlineService.UpdateFallbackProvider(
                LyricsFallbackProvider.None);
            var onlineResult = await onlineService.GetLyricsAsync(
                snapshot,
                CancellationToken.None);
            Assert(
                onlineResult.Status == LyricsLookupStatus.Found
                && onlineResult.Lines.Count == 1,
                "online lyrics lookup must create a cacheable result");
        }

        var offlineHandler = new ProbeHttpMessageHandler(_ =>
            new HttpResponseMessage(
                System.Net.HttpStatusCode.ServiceUnavailable));
        using var offlineClient = new HttpClient(offlineHandler)
        {
            BaseAddress = new Uri("https://lrclib.net")
        };
        using var offlineService = new LyricsService(
            offlineClient,
            temporaryDirectory);
        offlineService.UpdateFallbackProvider(LyricsFallbackProvider.None);
        var cachedResult = await offlineService.GetLyricsAsync(
            snapshot,
            CancellationToken.None);
        Assert(
            cachedResult.Status == LyricsLookupStatus.Found
            && cachedResult.Lines.Single().Text == "cached line"
            && offlineHandler.RequestCount == 0,
            "persistent lyrics cache must survive service restart and avoid a network request");
    }
    finally
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

void VerifyBrowserDownloadProgress()
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"dynamic-capsule-download-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        using var service = new BrowserDownloadProgressService(
            [temporaryDirectory]);
        var events = new List<CapsuleEvent>();
        var removed = new List<string>();
        service.EventChanged += events.Add;
        service.EventRemoved += removed.Add;

        var partialPath = Path.Combine(
            temporaryDirectory,
            "capsule-test.zip.crdownload");
        var targetPath = Path.Combine(
            temporaryDirectory,
            "capsule-test.zip");
        File.WriteAllBytes(partialPath, new byte[2048]);
        service.ScanNow();

        Assert(
            events.Count == 1
            && events[0].Kind == CapsuleEventKind.TaskProgress
            && events[0].TaskState == CapsuleTaskState.Running
            && events[0].Title == "capsule-test.zip"
            && events[0].Progress is null,
            "browser download monitor must publish a running indeterminate task");

        File.Move(partialPath, targetPath);
        service.ScanNow();
        Assert(
            events.Count == 2
            && events[^1].TaskState == CapsuleTaskState.Succeeded
            && events[^1].Progress == 1
            && removed.Count == 0,
            "browser download monitor must publish completion after final rename");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
}

void VerifyPinnedPriority()
{
    using var scheduler = new CapsuleEventScheduler();
    var pinned = CreateEvent(CapsuleEventKind.Timer, "timer", "计时器");
    var ordinaryHigh = CreateEvent(
        CapsuleEventKind.Notification,
        "mail",
        "邮件") with
    {
        EventId = "probe:ordinary-high",
        Priority = CapsuleEventPriority.High,
        CreatedAt = pinned.CreatedAt.AddSeconds(1)
    };
    var critical = CreateEvent(
        CapsuleEventKind.TaskProgress,
        "build",
        "构建") with
    {
        EventId = "probe:critical",
        Priority = CapsuleEventPriority.Critical,
        CreatedAt = pinned.CreatedAt.AddSeconds(2)
    };

    scheduler.Publish(pinned);
    scheduler.Publish(ordinaryHigh);
    scheduler.Pin(pinned.EventId);
    Assert(
        scheduler.ActiveEvent?.EventId == pinned.EventId,
        "pinned event must beat ordinary high priority");

    scheduler.Publish(critical);
    Assert(
        scheduler.ActiveEvent?.EventId == critical.EventId,
        "critical event must preempt pinned content");

    scheduler.Remove(critical.EventId);
    Assert(
        scheduler.ActiveEvent?.EventId == pinned.EventId,
        "pinned event must return after critical content is removed");
}

void VerifyPrimarySecondaryPresentation()
{
    using var scheduler = new CapsuleEventScheduler();
    var firstTask = CreateEvent(
        CapsuleEventKind.TaskProgress,
        "download",
        "下载") with
    {
        EventId = "probe:first-task",
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-2)
    };
    var secondTask = CreateEvent(
        CapsuleEventKind.Timer,
        "timer",
        "计时器") with
    {
        EventId = "probe:second-task",
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
    };

    scheduler.Publish(firstTask);
    scheduler.Publish(secondTask);
    Assert(
        scheduler.ActiveEvent?.EventId == secondTask.EventId,
        "newer equal-priority running task must become primary");
    Assert(
        scheduler.SecondaryEvent?.EventId == firstTask.EventId,
        "the other running task must occupy the secondary region");

    scheduler.Publish(secondTask with
    {
        TaskState = CapsuleTaskState.Succeeded,
        Progress = 1,
        Priority = CapsuleEventPriority.High
    });
    Assert(
        scheduler.ActiveEvent?.EventId == secondTask.EventId,
        "completed high-priority task must remain primary temporarily");
    Assert(
        scheduler.SecondaryEvent?.EventId == firstTask.EventId,
        "a running task must remain visible beside terminal primary content");
}

void VerifyBatchMerge()
{
    using var scheduler = new CapsuleEventScheduler();
    var presentationChanges = 0;
    scheduler.PresentedEventsChanged += (_, _) => presentationChanges++;

    var createdAt = DateTimeOffset.UtcNow;
    var download = CreateEvent(
        CapsuleEventKind.TaskProgress,
        "download",
        "下载") with
    {
        EventId = "probe:batch-download",
        Title = "旧下载状态",
        Progress = 0.1,
        CreatedAt = createdAt.AddSeconds(-2)
    };
    var updatedDownload = download with
    {
        Title = "最新下载状态",
        Progress = 0.65,
        CreatedAt = createdAt
    };
    var timer = CreateEvent(
        CapsuleEventKind.Timer,
        "timer",
        "计时器") with
    {
        EventId = "probe:batch-timer",
        CreatedAt = createdAt.AddSeconds(-1)
    };

    scheduler.PublishBatch([download, timer, updatedDownload]);

    Assert(
        presentationChanges == 1,
        "one batch must trigger at most one presentation update");
    Assert(
        scheduler.ActiveEvent?.EventId == updatedDownload.EventId
        && scheduler.ActiveEvent.Title == "最新下载状态"
        && scheduler.ActiveEvent.Progress == 0.65,
        "the last same-ID event in a batch must replace earlier values");
    Assert(
        scheduler.SecondaryEvent?.EventId == timer.EventId,
        "batch merging must keep a real secondary event");
    Assert(
        scheduler.Pin(timer.EventId)
        && scheduler.ActiveEvent?.EventId == timer.EventId,
        "a secondary event produced by a batch must remain pinnable");

    var changesBeforeReplacement = presentationChanges;
    var stopwatch = CreateEvent(
        CapsuleEventKind.Stopwatch,
        "stopwatch",
        "秒表") with
    {
        EventId = "probe:batch-stopwatch",
        CreatedAt = createdAt.AddSeconds(1)
    };
    scheduler.PublishBatch(
        [stopwatch],
        [timer.EventId, updatedDownload.EventId]);
    Assert(
        presentationChanges == changesBeforeReplacement + 1
        && scheduler.ActiveEvent?.EventId == stopwatch.EventId
        && scheduler.SecondaryEvent is null
        && scheduler.PinnedEventId is null,
        "batch removals and replacements must produce one coherent presentation");

    var changesAfterReplacement = presentationChanges;
    scheduler.PublishBatch([]);
    Assert(
        presentationChanges == changesAfterReplacement,
        "an empty batch must not trigger a presentation update");
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

static async Task<int> ProbeMediaSessionAsync()
{
    using var service = new MediaSessionService();
    var snapshotSource = new TaskCompletionSource<MediaSnapshot?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    string? status = null;
    service.StatusChanged += value => status = value;
    service.SnapshotChanged += snapshot =>
    {
        if (snapshot is not null)
        {
            snapshotSource.TrySetResult(snapshot);
        }
    };

    await service.StartAsync();
    var completed = await Task.WhenAny(
        snapshotSource.Task,
        Task.Delay(TimeSpan.FromSeconds(5)));
    var snapshot = completed == snapshotSource.Task
        ? await snapshotSource.Task
        : null;

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            Status = status,
            snapshot?.SourceAppUserModelId,
            snapshot?.SourceDisplayName,
            snapshot?.Title,
            snapshot?.Artist,
            snapshot?.AlbumTitle,
            snapshot?.LyricLine,
            PositionSeconds = snapshot?.Position.TotalSeconds,
            DurationSeconds = snapshot?.EndTime.TotalSeconds,
            ArtworkBytes = snapshot?.Artwork?.Length ?? 0
        },
        new JsonSerializerOptions { WriteIndented = true }));
    return snapshot is null ? 2 : 0;
}

void VerifyQqMusicSeekCommands()
{
    var forward = QqMusicSeekFallbackService.CreateCommand(
        TimeSpan.FromSeconds(20.2),
        TimeSpan.FromSeconds(45));
    Assert(
        forward is { Argument: "/forward", Seconds: 25 },
        "QQ Music fallback must translate a later target into a rounded forward command");

    var rewind = QqMusicSeekFallbackService.CreateCommand(
        TimeSpan.FromSeconds(80.6),
        TimeSpan.FromSeconds(30));
    Assert(
        rewind is { Argument: "/rewind", Seconds: 51 },
        "QQ Music fallback must translate an earlier target into a rounded rewind command");

    var noOp = QqMusicSeekFallbackService.CreateCommand(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30.5));
    Assert(
        noOp is null,
        "QQ Music fallback must ignore sub-second seek jitter");
}

static async Task<int> ProbeQqMusicLyricsAsync()
{
    using var service = new LyricsService();
    var snapshot = new MediaSnapshot(
        "QQMusic.exe",
        "QQ 音乐",
        "落下 (いっぱつにゅうこんver.)",
        "日向文",
        "cry",
        null,
        true,
        true,
        true,
        true,
        true,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(166),
        DateTimeOffset.UtcNow,
        null);
    var result = await service.GetLyricsAsync(
        snapshot,
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            Status = result.Status.ToString(),
            result.Source,
            LineCount = result.Lines.Count,
            FirstLine = result.Lines.FirstOrDefault(),
            ArtworkBytes = result.Artwork?.Length ?? 0
        },
        new JsonSerializerOptions { WriteIndented = true }));
    return result.Status is LyricsLookupStatus.Found
           && result.Lines.Count > 0
           && result.Artwork is { Length: > 0 }
        ? 0
        : 3;
}

static CapsuleEvent CreateEvent(
    CapsuleEventKind kind,
    string sourceId,
    string source)
{
    return new CapsuleEvent(
        $"probe:{sourceId}",
        kind,
        CapsuleEventPriority.Normal,
        sourceId,
        source,
        "敏感标题",
        new string('x', 100),
        0.5,
        kind is CapsuleEventKind.TaskProgress or CapsuleEventKind.Timer
            ? CapsuleTaskState.Running
            : null,
        EventPrivacyLevel.Full,
        DateTimeOffset.UtcNow,
        null);
}

internal sealed class ProbeHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
{
    internal int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(responseFactory(request));
    }
}
