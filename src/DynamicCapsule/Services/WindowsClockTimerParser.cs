using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicCapsule.Services;

internal enum WindowsClockTimerRunState
{
    Unknown,
    NotStarted,
    Running,
    Paused
}

internal static partial class WindowsClockTimerParser
{
    internal static WindowsClockTimerRunState ParseRunState(
        string? cardName,
        string? playPauseButtonName)
    {
        var card = cardName ?? string.Empty;
        var action = playPauseButtonName ?? string.Empty;

        if (ContainsAny(card, "未开始", "尚未开始", "not started"))
        {
            return WindowsClockTimerRunState.NotStarted;
        }

        if (ContainsAny(card, "已暂停", "paused")
            || ContainsAny(action, "已暂停"))
        {
            return WindowsClockTimerRunState.Paused;
        }

        if (ContainsAny(action, "暂停", "pause"))
        {
            return WindowsClockTimerRunState.Running;
        }

        if (ContainsAny(action, "开始", "继续", "start", "resume"))
        {
            return WindowsClockTimerRunState.Paused;
        }

        if (ContainsAny(card, "正在运行", "运行中", "running"))
        {
            return WindowsClockTimerRunState.Running;
        }

        return WindowsClockTimerRunState.Unknown;
    }

    internal static bool TryParseDuration(
        string? text,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim()
            .Replace('：', ':')
            .Replace(',', '.');
        if (normalized.StartsWith('-')
            || normalized.StartsWith('−'))
        {
            duration = TimeSpan.Zero;
            return true;
        }

        var colonMatch = ColonDurationRegex().Match(normalized);
        if (colonMatch.Success)
        {
            var parts = colonMatch.Value.Split(':');
            if (parts.Length is 2 or 3
                && parts.All(part => int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _)))
            {
                var values = parts
                    .Select(part => int.Parse(
                        part,
                        CultureInfo.InvariantCulture))
                    .ToArray();
                duration = parts.Length == 3
                    ? TimeSpan.FromHours(values[0])
                      + TimeSpan.FromMinutes(values[1])
                      + TimeSpan.FromSeconds(values[2])
                    : TimeSpan.FromMinutes(values[0])
                      + TimeSpan.FromSeconds(values[1]);
                return true;
            }
        }

        var totalSeconds = 0d;
        var matchedUnit = false;
        foreach (Match match in UnitDurationRegex().Matches(normalized))
        {
            if (!double.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            if (unit is "小时" or "小時" or "hour" or "hours" or "hr" or "hrs" or "h")
            {
                totalSeconds += value * 3600;
            }
            else if (unit is "分钟" or "分鐘" or "分" or "minute" or "minutes" or "min" or "mins" or "m")
            {
                totalSeconds += value * 60;
            }
            else
            {
                totalSeconds += value;
            }

            matchedUnit = true;
        }

        if (!matchedUnit || totalSeconds < 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    internal static string CreateEventId(string title, int ordinal)
    {
        var normalizedTitle = string.Join(
            ' ',
            title.Trim().ToLowerInvariant()
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{normalizedTitle}\n{ordinal}"));
        return $"windows-clock:{Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    internal static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var trailingSeconds = seconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{trailingSeconds:00}"
            : $"{minutes:00}:{trailingSeconds:00}";
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(
            candidate,
            StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}:)?\d{1,2}:\d{1,2}(?!\d)")]
    private static partial Regex ColonDurationRegex();

    [GeneratedRegex(
        @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>小时|小時|分钟|分鐘|分|秒钟|秒鐘|秒|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnitDurationRegex();
}
