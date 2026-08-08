namespace DynamicCapsule.Models;

internal sealed record CountdownTimerStatus(
    bool IsActive,
    bool IsPaused,
    string? EventId,
    string Title,
    TimeSpan Remaining)
{
    internal static readonly CountdownTimerStatus Inactive =
        new(false, false, null, string.Empty, TimeSpan.Zero);
}
