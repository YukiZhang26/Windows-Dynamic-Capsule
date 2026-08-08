namespace DynamicCapsule.Models;

internal sealed record LyricLine(
    TimeSpan Timestamp,
    string Text);

internal enum LyricsLookupStatus
{
    Found,
    Instrumental,
    UnsyncedOnly,
    NotFound,
    Unavailable
}

internal sealed record LyricsLookupResult(
    LyricsLookupStatus Status,
    IReadOnlyList<LyricLine> Lines,
    string Source)
{
    internal byte[]? Artwork { get; init; }
    internal TimeSpan Duration { get; init; }

    internal static LyricsLookupResult Empty(
        LyricsLookupStatus status,
        string source = "LRCLIB")
    {
        return new LyricsLookupResult(status, Array.Empty<LyricLine>(), source);
    }
}
