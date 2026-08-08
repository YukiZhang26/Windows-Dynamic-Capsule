namespace DynamicCapsule.Models;

internal enum CapsuleEventKind
{
    Notification,
    TaskProgress,
    Timer,
    Stopwatch,
    Bluetooth,
    WiFi
}

internal enum CapsuleEventPriority
{
    Low = 0,
    Normal = 10,
    High = 20,
    Critical = 30
}

internal enum CapsuleTaskState
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

internal sealed record CapsuleEvent(
    string EventId,
    CapsuleEventKind Kind,
    CapsuleEventPriority Priority,
    string SourceId,
    string Source,
    string Title,
    string Message,
    double? Progress,
    CapsuleTaskState? TaskState,
    EventPrivacyLevel PrivacyLevel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    internal static CapsuleEvent FromNotification(NotificationSnapshot notification)
    {
        return new CapsuleEvent(
            $"notification:{notification.Id}",
            CapsuleEventKind.Notification,
            CapsuleEventPriority.High,
            notification.AppUserModelId,
            notification.AppDisplayName,
            notification.Title,
            notification.Body,
            null,
            null,
            EventPrivacyLevel.Full,
            notification.CreationTime,
            DateTimeOffset.UtcNow.AddSeconds(6));
    }
}
