using DynamicCapsule.Models;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicCapsule.Services;

internal sealed class NotificationService : IDisposable
{
    private const string PhoneLinkNotificationMarker =
        "YourPhoneNotifications_";
    private static readonly TimeSpan NotificationPollInterval =
        TimeSpan.FromMilliseconds(800);
    private static readonly IReadOnlyDictionary<string, string>
        PhoneAppDisplayNames = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["com.tencent.xin"] = "微信",
            ["com.tencent.mqq"] = "QQ",
            ["com.apple.MobileSMS"] = "信息",
            ["com.apple.mobilemail"] = "邮件",
            ["com.apple.mobilecal"] = "日历",
            ["com.apple.reminders"] = "提醒事项",
            ["com.apple.shortcuts"] = "快捷指令",
            ["com.apple.Fitness"] = "健身",
            ["com.apple.Health"] = "健康",
            ["com.alipay.iphoneclient"] = "支付宝",
            ["com.ccb.ccbDemo"] = "中国建设银行",
            ["net.whatsapp.WhatsApp"] = "WhatsApp",
            ["com.google.Gmail"] = "Gmail",
            ["com.gotokeep.keep"] = "Keep",
            ["com.netease.mailmaster"] = "网易邮箱大师",
            ["com.transferwise.Transferwise"] = "Wise",
            ["com.travelsky.umetrip"] = "航旅纵横",
            ["com.burbn.instagram"] = "Instagram",
            ["com.facebook.Messenger"] = "Messenger",
            ["ph.telegra.Telegraph"] = "Telegram",
            ["jp.co.sony.songpal.mdr"] = "Sony 耳机"
        };
    private readonly HashSet<uint> _knownNotificationIds = [];
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private UserNotificationListener? _listener;
    private System.Threading.Timer? _pollTimer;
    private int _pollInProgress;
    private bool _disposed;

    internal event Action<NotificationSnapshot>? NotificationReceived;
    internal event Action<uint>? NotificationRemoved;
    internal event Action<NotificationListenerStatus, string>? StatusChanged;

    internal async Task<NotificationListenerStatus> StartAsync(bool requestAccess)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ApiInformation.IsTypePresent(
                "Windows.UI.Notifications.Management.UserNotificationListener"))
        {
            return PublishStatus(
                NotificationListenerStatus.Unsupported,
                "当前 Windows 版本不支持通知监听");
        }

        if (!HasPackageIdentity())
        {
            return PublishStatus(
                NotificationListenerStatus.PackageIdentityRequired,
                "通知监听需要带 userNotificationListener 能力的 MSIX 包身份");
        }

        try
        {
            _listener = UserNotificationListener.Current;
            var accessStatus = _listener.GetAccessStatus();

            if (accessStatus is UserNotificationListenerAccessStatus.Unspecified
                && requestAccess)
            {
                // Microsoft requires RequestAccessAsync to be invoked from a UI thread.
                accessStatus = await _listener.RequestAccessAsync();
            }

            var status = MapAccessStatus(accessStatus);
            if (status is not NotificationListenerStatus.Allowed)
            {
                return PublishStatus(status, GetAccessStatusText(status));
            }

            _listener.NotificationChanged += OnNotificationChanged;
            await SynchronizeAsync(emitNewNotifications: false);
            StartPolling();

            return PublishStatus(
                NotificationListenerStatus.Allowed,
                "通知监听已启用（独立于 Windows 请勿打扰）");
        }
        catch (Exception exception)
        {
            _listener = null;
            return PublishStatus(
                NotificationListenerStatus.Unavailable,
                $"通知监听不可用：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_listener is not null)
        {
            _listener.NotificationChanged -= OnNotificationChanged;
            _listener = null;
        }

        _knownNotificationIds.Clear();
    }

    private void OnNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (args.ChangeKind is UserNotificationChangedKind.Removed)
        {
            lock (_knownNotificationIds)
            {
                _knownNotificationIds.Remove(args.UserNotificationId);
            }

            NotificationRemoved?.Invoke(args.UserNotificationId);
            return;
        }

        _ = SynchronizeAsync(emitNewNotifications: true);
    }

    private void StartPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(
            OnPollTimer,
            null,
            NotificationPollInterval,
            NotificationPollInterval);
    }

    private void OnPollTimer(object? state)
    {
        if (_disposed
            || Interlocked.Exchange(ref _pollInProgress, 1) != 0)
        {
            return;
        }

        _ = PollNotificationsAsync();
    }

    private async Task PollNotificationsAsync()
    {
        try
        {
            // Query notification history directly as a fallback. Windows
            // Focus Assist / Do Not Disturb may suppress its own banner, but
            // it must not become an input to the capsule's independent mode.
            await SynchronizeAsync(emitNewNotifications: true);
        }
        finally
        {
            Volatile.Write(ref _pollInProgress, 0);
        }
    }

    private async Task SynchronizeAsync(bool emitNewNotifications)
    {
        if (_disposed || _listener is null)
        {
            return;
        }

        await _syncGate.WaitAsync();
        try
        {
            if (_disposed
                || _listener is null
                || _listener.GetAccessStatus()
                is not UserNotificationListenerAccessStatus.Allowed)
            {
                PublishStatus(
                    NotificationListenerStatus.Denied,
                    "通知权限已被撤销，请在 Windows 设置中重新授权");
                return;
            }

            var notifications = await _listener.GetNotificationsAsync(
                NotificationKinds.Toast);
            var currentIds = notifications
                .Select(notification => notification.Id)
                .ToHashSet();

            lock (_knownNotificationIds)
            {
                _knownNotificationIds.RemoveWhere(id => !currentIds.Contains(id));
            }

            foreach (var notification in notifications
                         .OrderBy(item => item.CreationTime))
            {
                var isNew = false;
                lock (_knownNotificationIds)
                {
                    isNew = _knownNotificationIds.Add(notification.Id);
                }

                if (!emitNewNotifications || !isNew)
                {
                    continue;
                }

                var snapshot = TryCreateSnapshot(notification);
                if (snapshot is not null)
                {
                    NotificationReceived?.Invoke(snapshot);
                }
            }
        }
        catch (Exception exception)
        {
            PublishStatus(
                NotificationListenerStatus.Unavailable,
                $"同步通知失败：{exception.Message}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static NotificationSnapshot? TryCreateSnapshot(
        UserNotification notification)
    {
        try
        {
            var binding = notification.Notification.Visual.GetBinding(
                KnownNotificationBindings.ToastGeneric);
            var textElements = binding?.GetTextElements()
                .Select(element => NormalizeText(element.Text))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray()
                ?? [];

            var appDisplayName = NormalizeText(
                notification.AppInfo.DisplayInfo.DisplayName);
            var appUserModelId = NormalizeText(
                notification.AppInfo.AppUserModelId);
            var source = ResolveNotificationSource(
                appUserModelId,
                appDisplayName);
            var title = textElements.FirstOrDefault();
            var body = string.Join(" · ", textElements.Skip(1));

            if (string.IsNullOrWhiteSpace(title)
                && string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return new NotificationSnapshot(
                notification.Id,
                source.SourceId,
                source.DisplayName,
                string.IsNullOrWhiteSpace(title)
                    ? "新通知"
                    : title,
                Truncate(body, 180),
                notification.CreationTime);
        }
        catch
        {
            // A malformed notification must not block other notifications.
            return null;
        }
    }

    private static (string SourceId, string DisplayName)
        ResolveNotificationSource(
            string appUserModelId,
            string appDisplayName)
    {
        var markerIndex = appUserModelId.IndexOf(
            PhoneLinkNotificationMarker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var mobileAppId = appUserModelId[
                (markerIndex + PhoneLinkNotificationMarker.Length)..]
                .Trim();
            if (!string.IsNullOrWhiteSpace(mobileAppId))
            {
                return (
                    mobileAppId,
                    PhoneAppDisplayNames.TryGetValue(
                        mobileAppId,
                        out var friendlyName)
                        ? friendlyName
                        : GetFallbackPhoneAppName(mobileAppId));
            }
        }

        var sourceId = string.IsNullOrWhiteSpace(appUserModelId)
            ? appDisplayName
            : appUserModelId;
        var displayName = string.IsNullOrWhiteSpace(appDisplayName)
            ? "Windows 通知"
            : appDisplayName;
        return (sourceId, displayName);
    }

    private static string GetFallbackPhoneAppName(string mobileAppId)
    {
        var candidate = mobileAppId
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(candidate)
            || Guid.TryParse(candidate, out _)
            || candidate.Length < 2)
        {
            return "iPhone 通知";
        }

        candidate = candidate.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(candidate)
            ? "iPhone 通知"
            : $"{candidate} · iPhone";
    }

    private NotificationListenerStatus PublishStatus(
        NotificationListenerStatus status,
        string message)
    {
        StatusChanged?.Invoke(status, message);
        return status;
    }

    private static NotificationListenerStatus MapAccessStatus(
        UserNotificationListenerAccessStatus status)
    {
        return status switch
        {
            UserNotificationListenerAccessStatus.Allowed
                => NotificationListenerStatus.Allowed,
            UserNotificationListenerAccessStatus.Denied
                => NotificationListenerStatus.Denied,
            _ => NotificationListenerStatus.Unspecified
        };
    }

    private static string GetAccessStatusText(NotificationListenerStatus status)
    {
        return status switch
        {
            NotificationListenerStatus.Denied
                => "通知权限已拒绝，请在 Windows 设置中手动授权",
            _ => "尚未授予通知读取权限"
        };
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id.FullName;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static string NormalizeText(string? value)
    {
        return string.Join(
            ' ',
            (value ?? string.Empty).Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maximumLength)
        {
            return value;
        }

        return $"{value[..(maximumLength - 1)].TrimEnd()}…";
    }
}
