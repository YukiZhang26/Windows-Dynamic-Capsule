namespace DynamicCapsule.Models;

internal sealed record StopwatchStatus(
    bool IsActive,
    bool IsPaused,
    string? EventId,
    TimeSpan Elapsed)
{
    internal static readonly StopwatchStatus Inactive =
        new(false, false, null, TimeSpan.Zero);
}
