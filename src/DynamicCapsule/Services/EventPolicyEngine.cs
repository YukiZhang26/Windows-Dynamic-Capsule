using DynamicCapsule.Models;

namespace DynamicCapsule.Services;

internal enum EventPolicyReason
{
    Allowed,
    BlockedSource,
    SourceNotAllowed
}

internal sealed record EventPolicyDecision(
    bool IsAllowed,
    CapsuleEvent? CapsuleEvent,
    EventPolicyReason Reason);

internal sealed class EventPolicyEngine
{
    private const int SummaryMaximumLength = 72;

    internal EventPolicyDecision Evaluate(
        CapsuleEvent capsuleEvent,
        AppSettings settings)
    {
        if (capsuleEvent.Kind == CapsuleEventKind.Notification)
        {
            if (MatchesSourceList(
                    capsuleEvent,
                    settings.NotificationBlockList))
            {
                return Deny(EventPolicyReason.BlockedSource);
            }

            if (settings.NotificationAllowList.Length > 0
                && !MatchesSourceList(
                    capsuleEvent,
                    settings.NotificationAllowList))
            {
                return Deny(EventPolicyReason.SourceNotAllowed);
            }
        }

        return new EventPolicyDecision(
            true,
            ApplyPrivacy(capsuleEvent, settings.PrivacyLevel),
            EventPolicyReason.Allowed);
    }

    private static EventPolicyDecision Deny(EventPolicyReason reason)
    {
        return new EventPolicyDecision(false, null, reason);
    }

    private static bool MatchesSourceList(
        CapsuleEvent capsuleEvent,
        IEnumerable<string> sources)
    {
        return sources.Any(source =>
            string.Equals(
                source,
                capsuleEvent.SourceId,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                source,
                capsuleEvent.Source,
                StringComparison.OrdinalIgnoreCase));
    }

    private static CapsuleEvent ApplyPrivacy(
        CapsuleEvent capsuleEvent,
        EventPrivacyLevel privacyLevel)
    {
        return privacyLevel switch
        {
            EventPrivacyLevel.Full => capsuleEvent with
            {
                PrivacyLevel = privacyLevel
            },
            EventPrivacyLevel.Summary => capsuleEvent with
            {
                Message = Truncate(capsuleEvent.Message, SummaryMaximumLength),
                PrivacyLevel = privacyLevel
            },
            EventPrivacyLevel.Masked => capsuleEvent with
            {
                Title = capsuleEvent.Kind switch
                {
                    CapsuleEventKind.Notification => "通知内容已隐藏",
                    CapsuleEventKind.Timer => "计时内容已隐藏",
                    CapsuleEventKind.Bluetooth => "蓝牙状态已隐藏",
                    CapsuleEventKind.WiFi => "网络状态已隐藏",
                    _ => "任务内容已隐藏"
                },
                Message = "内容已隐藏",
                PrivacyLevel = privacyLevel
            },
            EventPrivacyLevel.IconOnly => capsuleEvent with
            {
                Title = string.Empty,
                Message = string.Empty,
                Progress = null,
                PrivacyLevel = privacyLevel
            },
            _ => capsuleEvent with
            {
                Message = Truncate(capsuleEvent.Message, SummaryMaximumLength),
                PrivacyLevel = EventPrivacyLevel.Summary
            }
        };
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
