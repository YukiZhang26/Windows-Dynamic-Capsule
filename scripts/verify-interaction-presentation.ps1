[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Read-Source {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    return Get-Content `
        -LiteralPath (Join-Path $repositoryRoot $RelativePath) `
        -Raw `
        -Encoding utf8
}

function Read-MethodBody {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $MethodSignature,

        [Parameter(Mandatory = $true)]
        [string] $NextMethodSignature
    )

    $start = $Source.IndexOf(
        $MethodSignature,
        [StringComparison]::Ordinal)
    $end = $Source.IndexOf(
        $NextMethodSignature,
        $start + $MethodSignature.Length,
        [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) {
        throw "Unable to isolate method: $MethodSignature"
    }

    return $Source.Substring($start, $end - $start)
}

$windowXaml = Read-Source "src\DynamicCapsule\CapsuleWindow.xaml"
$windowCode = Read-Source "src\DynamicCapsule\CapsuleWindow.xaml.cs"
$settingsXaml = Read-Source "src\DynamicCapsule\SettingsWindow.xaml"
$settingsCode = Read-Source "src\DynamicCapsule\SettingsWindow.xaml.cs"
$appCode = Read-Source "src\DynamicCapsule\App.xaml.cs"
$schedulerCode = Read-Source (
    "src\DynamicCapsule\Services\CapsuleEventScheduler.cs")
$clockTimerCode = Read-Source (
    "src\DynamicCapsule\Services\WindowsClockTimerSyncService.cs")
$countdownTimerCode = Read-Source (
    "src\DynamicCapsule\Services\CountdownTimerService.cs")
$stopwatchCode = Read-Source (
    "src\DynamicCapsule\Services\StopwatchService.cs")
$mediaServiceCode = Read-Source (
    "src\DynamicCapsule\Services\MediaSessionService.cs")
$notificationServiceCode = Read-Source (
    "src\DynamicCapsule\Services\NotificationService.cs")
$lyricsServiceCode = Read-Source (
    "src\DynamicCapsule\Services\LyricsService.cs")
$nativeCode = Read-Source "src\DynamicCapsule\NativeMethods.cs"

$sizeChangedBody = Read-MethodBody `
    -Source $windowCode `
    -MethodSignature "private void OnSizeChanged" `
    -NextMethodSignature "private void ToggleExpanded"
$compactRingBody = Read-MethodBody `
    -Source $windowCode `
    -MethodSignature "private static double GetCompactRingValue" `
    -NextMethodSignature "private static bool IsBrowserDownload"

$checks = @(
    [PSCustomObject]@{
        Name = "Collapsed media includes artwork and synchronized lyrics"
        Passed =
            $windowXaml.Contains(
                'x:Name="MediaCompactArtworkImage"') -and
            $windowXaml.Contains(
                'x:Name="MediaCompactLyricsText"') -and
            $windowCode.Contains(
                "MediaCompactArtworkImage.Source = artwork;") -and
            $windowCode.Contains(
                "MediaCompactLyricsText.Text = LyricsText.Text;") -and
            $windowCode.Contains("MediaCompactHeight = 52")
    },
    [PSCustomObject]@{
        Name = "Right click expands capsule-native quick actions"
        Passed =
            $windowXaml.Contains(
                'x:Name="QuickMenuContent"') -and
            $windowXaml.Contains(
                'x:Name="QuickToggleButton"') -and
            $windowXaml.Contains(
                'x:Name="QuickDoNotDisturbButton"') -and
            $windowXaml.Contains("QuickMenuButtonStyle") -and
            -not $windowXaml.Contains('<Border.ContextMenu>') -and
            $windowCode.Contains("SetQuickMenuOpen") -and
            $windowCode.Contains("OnContextCountdownClick") -and
            $windowCode.Contains("OnContextCountdownFiveClick") -and
            $windowCode.Contains("OnContextCountdownFifteenClick") -and
            $windowCode.Contains("OnContextCountdownTwentyFiveClick") -and
            $windowXaml.Contains(
                'PreviewMouseRightButtonDown="OnCapsulePreviewMouseRightButtonDown"') -and
            $windowCode.Contains("OnCapsulePreviewMouseRightButtonDown") -and
            $windowCode.Contains("OnContextWindowsStopwatchClick") -and
            $windowCode.Contains("OnContextDoNotDisturbClick") -and
            $windowCode.Contains("DoNotDisturbChangeRequested") -and
            $windowCode.Contains("OnContextExitClick")
    },
    [PSCustomObject]@{
        Name = "Stopwatch participates in the long-running task model"
        Passed =
            ($stopwatchCode -match 'CapsuleEventKind\.Stopwatch') -and
            ($stopwatchCode -match 'TogglePause') -and
            ($stopwatchCode -match 'CreateTerminalEvent') -and
            ($windowCode -match 'CapsuleEventKind\.Stopwatch') -and
            ($schedulerCode -match 'CapsuleEventKind\.Stopwatch')
    },
    [PSCustomObject]@{
        Name = "Primary-secondary combinations remain event-kind agnostic"
        Passed =
            $schedulerCode.Contains("SelectPresentedEvents") -and
            $schedulerCode.Contains("IsSecondaryCandidate") -and
            $schedulerCode.Contains("CapsuleEventKind.TaskProgress") -and
            $schedulerCode.Contains("CapsuleEventKind.Timer") -and
            $schedulerCode.Contains("CapsuleEventKind.Stopwatch") -and
            $windowCode.Contains("ApplySecondaryEvent")
    },
    [PSCustomObject]@{
        Name = "Timer has pause-resume and cancel actions"
        Passed =
            $windowXaml.Contains(
                'x:Name="TimerPauseResumeButton"') -and
            $windowXaml.Contains(
                'x:Name="TimerCancelButton"') -and
            $windowCode.Contains(
                "ToggleCountdownPause();") -and
            $windowCode.Contains(
                "CancelCountdown();")
    },
    [PSCustomObject]@{
        Name = "Scheduler exposes primary and secondary events"
        Passed =
            $schedulerCode.Contains("SecondaryEvent") -and
            $schedulerCode.Contains("PresentedEventsChanged") -and
            $schedulerCode.Contains("IsSecondaryCandidate")
    },
    [PSCustomObject]@{
        Name = "Secondary task region is asymmetric and promotable"
        Passed =
            $windowXaml.Contains(
                'x:Name="SecondaryEventContent"') -and
            $windowCode.Contains("DualTaskWidth = 500") -and
            $windowCode.Contains(
                "_eventScheduler.Pin(_secondaryCapsuleEvent.EventId)")
    },
    [PSCustomObject]@{
        Name = "Long-running events collapse into progress rings"
        Passed =
            $windowXaml.Contains(
                'x:Name="EventCompactContent"') -and
            $windowXaml.Contains(
                'x:Name="PrimaryCompactProgressPath"') -and
            $windowCode.Contains(
                "ScheduleEventCollapse(TimeSpan.FromMilliseconds(2400))") -and
            $windowCode.Contains("CreateProgressArcGeometry") -and
            $windowCode.Contains("AnimateCompactEventDismissal")
    },
    [PSCustomObject]@{
        Name = "Dual compact events keep a primary-secondary hierarchy"
        Passed =
            $windowXaml.Contains(
                'x:Name="SecondaryCompactProgressPath"') -and
            $windowCode.Contains("EventCompactWidth = 132") -and
            $windowCode.Contains("DualEventCompactWidth = 176") -and
            $windowCode.Contains("SecondaryCompactPanel.Visibility")
    },
    [PSCustomObject]@{
        Name = "Media stays primary beside a compact live activity"
        Passed =
            $windowXaml.Contains(
                'x:Name="MediaEventCompactContent"') -and
            $windowXaml.Contains(
                'x:Name="MediaEventCompactArtworkImage"') -and
            $windowXaml.Contains(
                'x:Name="MediaEventCompactProgressPath"') -and
            $windowCode.Contains("IsMediaEventCompact") -and
            $windowCode.Contains("ApplyMediaEventCompact") -and
            $windowCode.Contains("MediaEventCompactWidth = 176")
    },
    [PSCustomObject]@{
        Name = "Combined compact media and timer have independent targets"
        Passed =
            $windowXaml.Contains(
                'x:Name="MediaEventCompactMusicPanel"') -and
            $windowXaml.Contains(
                'x:Name="MediaEventCompactActivityPanel"') -and
            $windowCode.Contains("SetMediaEventExpanded(true);") -and
            $windowCode.Contains("SetLongRunningEventExpanded(true);") -and
            $windowCode.Contains("_isMediaExpandedOverEvent")
    },
    [PSCustomObject]@{
        Name = "Timer progress rings fill with elapsed time"
        Passed =
            $countdownTimerCode.Contains(
                "1 - (remaining.TotalMilliseconds / _duration.TotalMilliseconds)") -and
            $clockTimerCode.Contains(
                "1 - remaining.TotalMilliseconds") -and
            $compactRingBody.Contains("Math.Clamp(") -and
            $compactRingBody.Contains("capsuleEvent.Progress") -and
            -not $compactRingBody.Contains(
                "return 1 - Math.Clamp(capsuleEvent.Progress")
    },
    [PSCustomObject]@{
        Name = "Completed timers persist with an explicit terminate action"
        Passed =
            $countdownTimerCode.Contains(
                "TimeSpan? visibleDuration") -and
            $clockTimerCode.Contains(
                "private static CapsuleEvent CreateCompletedEvent") -and
            $windowCode.Contains(
                "TimerCancelButton.Content =") -and
            $windowCode.Contains(
                "if (isTerminalTimeEvent)") -and
            $windowCode.Contains(
                "_activeCapsuleEvent.Kind == CapsuleEventKind.Timer")
    },
    [PSCustomObject]@{
        Name = "Windows Clock reset waits for a confirmed pause"
        Passed =
            $clockTimerCode.Contains("PauseRequested") -and
            $clockTimerCode.Contains("RequeueControlRequest") -and
            $clockTimerCode.Contains("if (timer.IsPaused)") -and
            $clockTimerCode.Contains("ObservedStopwatch") -and
            $clockTimerCode.Contains("CreateStopwatchEvent") -and
            $clockTimerCode.Contains("OpenStopwatchAsync") -and
            $clockTimerCode.Contains("StopwatchNavigationViewItem") -and
            $clockTimerCode.Contains("SelectionItemPattern") -and
            $clockTimerCode.Contains("windows-clock:stopwatch") -and
            $clockTimerCode.Contains("request.Attempts >= 6")
    },
    [PSCustomObject]@{
        Name = "Notifications expand through the capsule event surface"
        Passed =
            $windowXaml.Contains('x:Name="EventAlertContent"') -and
            $windowCode.Contains("OnNotificationReceived") -and
            $windowCode.Contains(
                "CapsuleEventKind.Notification") -and
            $windowCode.Contains("AnimatePrimaryEventContent")
    },
    [PSCustomObject]@{
        Name = "Capsule remains no-activate while handling notifications"
        Passed =
            $windowCode.Contains("NativeMethods.WsExNoActivate") -and
            $nativeCode.Contains("WsExNoActivate")
    },
    [PSCustomObject]@{
        Name = "Notification settings expose status and local preview"
        Passed =
            $settingsXaml.Contains(
                'x:Name="NotificationStatusTitleText"') -and
            $settingsXaml.Contains(
                'x:Name="PreviewNotificationButton"') -and
            $settingsXaml.Contains(
                'x:Name="OpenNotificationSettingsButton"') -and
            $settingsCode.Contains(
                "ms-settings:privacy-notifications") -and
            $settingsCode.Contains(
                "NotificationPreviewRequested") -and
            $windowCode.Contains(
                "ShowNotificationPreview")
    },
    [PSCustomObject]@{
        Name = "Capsule notifications remain independent of Windows DND"
        Passed =
            $settingsXaml.Contains(
                'x:Name="DoNotDisturbCheckBox"') -and
            $settingsXaml.Contains(
                'AutomationProperties.HelpText=') -and
            $notificationServiceCode.Contains(
                "NotificationPollInterval") -and
            $notificationServiceCode.Contains(
                "SynchronizeAsync(emitNewNotifications: true)") -and
            $notificationServiceCode.Contains(
                "Focus Assist / Do Not Disturb may suppress") -and
            -not $notificationServiceCode.Contains(
                "FocusAssist")
    },
    [PSCustomObject]@{
        Name = "WPF owns the complete button press lifecycle"
        Passed =
            $windowXaml.Contains(
                'MouseLeftButtonUp="OnWindowMouseLeftButtonUp"') -and
            $windowXaml.Contains(
                'Click="OnPreviousButtonClick"') -and
            $windowXaml.Contains(
                'Click="OnPlayPauseButtonClick"') -and
            $windowXaml.Contains(
                'Click="OnNextButtonClick"') -and
            $windowXaml.Contains('x:Name="PowerToggleButton"') -and
            $windowXaml.Contains(
                'Click="OnPowerToggleButtonClick"') -and
            $windowCode.Contains("ExitRequested?.Invoke();") -and
            -not $windowCode.Contains("HandleNativeLeftButtonUp") -and
            -not $windowCode.Contains("case NativeMethods.WmLeftButtonUp:") -and
            $appCode.Contains(
                "_capsuleWindow.ExitRequested += OnExitRequested;") -and
            $appCode.Contains("RequestExit();")
    },
    [PSCustomObject]@{
        Name = "Expanded media exposes an adjustable timeline"
        Passed =
            $windowXaml.Contains('x:Name="MediaProgressHitTarget"') -and
            $windowXaml.Contains('x:Name="MediaProgressFill"') -and
            $windowXaml.Contains('Background="White"') -and
            $windowXaml.Contains('x:Name="MediaElapsedText"') -and
            $windowXaml.Contains('x:Name="MediaRemainingText"') -and
            $windowCode.Contains("OnMediaProgressPreviewMouseLeftButtonUp") -and
            $windowCode.Contains("OnMediaProgressPreviewMouseMove") -and
            $windowCode.Contains("MediaProgressHitTarget.CaptureMouse()") -and
            $windowCode.Contains("SetMediaProgressFromPointer") -and
            $windowCode.Contains("ShouldPreservePendingMediaSeek") -and
            $windowCode.Contains("GetEffectiveMediaDuration") -and
            $lyricsServiceCode.Contains("Duration = qqMusicResult.Duration") -and
            $windowCode.Contains("_mediaService.SeekAsync(target)") -and
            $mediaServiceCode.Contains("TryChangePlaybackPositionAsync")
    },
    [PSCustomObject]@{
        Name = "Artwork decoding is cached across timeline refreshes"
        Passed =
            $windowCode.Contains("ArtworkBytesMatch") -and
            $windowCode.Contains("_hasAppliedArtwork") -and
            $windowXaml.Contains('CacheMode="BitmapCache"') -and
            $windowXaml.Contains('RenderOptions.BitmapScalingMode="LowQuality"')
    },
    [PSCustomObject]@{
        Name = "Phone Link is not opened automatically"
        Passed =
            -not $settingsXaml.Contains(
                'x:Name="LaunchPhoneLinkOnStartupCheckBox"') -and
            -not $appCode.Contains("LaunchPhoneLinkAfterStartup") -and
            -not (Test-Path -LiteralPath (
                Join-Path $repositoryRoot (
                    "src\DynamicCapsule\Services\PhoneLinkLauncherService.cs")))
    },
    [PSCustomObject]@{
        Name = "Morph uses a fixed transparent host without native resize churn"
        Passed =
            $sizeChangedBody.Contains(
                "if (_isAnimatingSize)") -and
            $windowXaml.Contains('Width="530"') -and
            $windowXaml.Contains('Height="286"') -and
            $windowXaml.Contains('x:Name="CapsuleSurface"') -and
            $windowCode.Contains(
                "private const double HostWidth = DualTaskWidth + SideArrowLaneWidth;") -and
            $windowCode.Contains(
                "private const double HostHeight = MediaExpandedHeight + MaximumTopGap;") -and
            $windowCode.Contains(
                "Width = HostWidth;") -and
            $windowCode.Contains(
                "Height = HostHeight;") -and
            $windowCode.Contains(
                "Height = HostHeight;") -and
            -not $windowCode.Contains(
                "Width = containerWidth;") -and
            $nativeCode.Contains("WmNcHitTest") -and
            $nativeCode.Contains("HtTransparent") -and
            $windowCode.Contains(
                "CapsuleSurface.ActualWidth") -and
            $windowXaml.Contains('ClipToBounds="True"')
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize
if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) interaction presentation check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    WindowsOpened = 0
    SystemSettingsModified = $false
}
