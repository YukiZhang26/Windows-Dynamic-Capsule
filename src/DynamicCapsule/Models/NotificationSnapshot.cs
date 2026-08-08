namespace DynamicCapsule.Models;

internal sealed record NotificationSnapshot(
    uint Id,
    string AppUserModelId,
    string AppDisplayName,
    string Title,
    string Body,
    DateTimeOffset CreationTime);

internal enum NotificationListenerStatus
{
    Unsupported,
    PackageIdentityRequired,
    Unspecified,
    Allowed,
    Denied,
    Unavailable
}
