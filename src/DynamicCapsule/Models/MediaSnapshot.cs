namespace DynamicCapsule.Models;

internal sealed record MediaSnapshot(
    string SourceAppUserModelId,
    string SourceDisplayName,
    string Title,
    string Artist,
    string AlbumTitle,
    string? LyricLine,
    bool IsPlaying,
    bool CanPlay,
    bool CanPause,
    bool CanGoPrevious,
    bool CanGoNext,
    TimeSpan Position,
    TimeSpan EndTime,
    DateTimeOffset TimelineUpdatedAt,
    byte[]? Artwork);
