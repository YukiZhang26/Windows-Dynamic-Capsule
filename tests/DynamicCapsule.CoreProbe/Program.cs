using DynamicCapsule.Models;
using DynamicCapsule.Services;
using System.IO;
using System.Net.Http;
using System.Text.Json;

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
await VerifyPersistentLyricsCacheAsync();
VerifyWindowsClockTimerParsing();
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
    "Core probe passed: source rules, privacy, batch scheduling, settings, persistent lyrics cache, and Windows Clock timer parsing.");
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
            window.CapsuleSurface.Width = 440;
            window.CapsuleSurface.Height = 238;
            window.ExpandedContent.Visibility =
                System.Windows.Visibility.Visible;
            window.ExpandedContent.Opacity = 1;
            window.CapsuleSurface.Measure(
                new System.Windows.Size(440, 238));
            window.CapsuleSurface.Arrange(
                new System.Windows.Rect(0, 0, 440, 238));
            window.CapsuleSurface.UpdateLayout();

            var playButtonOrigin = window.PlayPauseButton.TranslatePoint(
                new System.Windows.Point(),
                window.CapsuleSurface);
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
