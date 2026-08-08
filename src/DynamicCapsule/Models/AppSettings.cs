namespace DynamicCapsule.Models;

internal enum MonitorTargetMode
{
    FollowActiveWindow,
    PrimaryDisplay
}

internal enum EventPrivacyLevel
{
    Full,
    Summary,
    Masked,
    IconOnly
}

internal sealed record AppSettings
{
    internal const int CurrentSchemaVersion = 3;
    internal const double DefaultTopGap = 10;
    internal const double MaximumTopGap = 48;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public MonitorTargetMode MonitorTarget { get; init; } =
        MonitorTargetMode.FollowActiveWindow;
    public double TopGap { get; init; } = DefaultTopGap;
    public bool EnableAnimations { get; init; } = true;
    public bool HideInFullscreen { get; init; } = true;
    public bool DoNotDisturb { get; init; }
    public bool TopStashed { get; init; }
    public bool StartWithWindows { get; init; }
    public EventPrivacyLevel PrivacyLevel { get; init; } =
        EventPrivacyLevel.Summary;
    public string[] NotificationAllowList { get; init; } = [];
    public string[] NotificationBlockList { get; init; } = [];

    internal AppSettings Normalize()
    {
        var monitorTarget = Enum.IsDefined(MonitorTarget)
            ? MonitorTarget
            : MonitorTargetMode.FollowActiveWindow;
        var topGap = double.IsFinite(TopGap)
            ? Math.Clamp(TopGap, 0, MaximumTopGap)
            : DefaultTopGap;
        var privacyLevel = Enum.IsDefined(PrivacyLevel)
            ? PrivacyLevel
            : EventPrivacyLevel.Summary;

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            MonitorTarget = monitorTarget,
            TopGap = topGap,
            PrivacyLevel = privacyLevel,
            NotificationAllowList = NormalizeSourceList(NotificationAllowList),
            NotificationBlockList = NormalizeSourceList(NotificationBlockList)
        };
    }

    private static string[] NormalizeSourceList(IEnumerable<string>? sources)
    {
        return (sources ?? [])
            .Select(source => source?.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source!.Length <= 256
                ? source
                : source[..256])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }
}
