using DynamicCapsule.Models;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicCapsule.Services;

internal sealed class MediaSessionService : IDisposable
{
    private const int MaximumArtworkBytes = 12 * 1024 * 1024;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private string? _artworkIdentity;
    private byte[]? _cachedArtwork;
    private bool _disposed;

    internal event Action<MediaSnapshot?>? SnapshotChanged;
    internal event Action<string>? StatusChanged;

    internal async Task<bool> StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            StatusChanged?.Invoke("正在连接系统媒体会话");

            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;

            BindCurrentSession();
            await RefreshSnapshotAsync();

            return true;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"媒体会话不可用：{exception.Message}");
            SnapshotChanged?.Invoke(null);
            return false;
        }
    }

    internal Task<bool> TogglePlayPauseAsync()
    {
        return ExecuteControlAsync(session => session.TryTogglePlayPauseAsync());
    }

    internal Task<bool> GoToPreviousAsync()
    {
        return ExecuteControlAsync(session => session.TrySkipPreviousAsync());
    }

    internal Task<bool> GoToNextAsync()
    {
        return ExecuteControlAsync(session => session.TrySkipNextAsync());
    }

    internal async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _currentSession;
        if (_disposed || session is null)
        {
            return false;
        }

        var clampedPosition = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position;
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var succeeded = playbackInfo.Controls.IsPlaybackPositionEnabled
                            && await session.TryChangePlaybackPositionAsync(
                                clampedPosition.Ticks);
            var usedQqMusicFallback = false;
            if (!succeeded
                && QqMusicSeekFallbackService.IsQqMusic(
                    session.SourceAppUserModelId))
            {
                var timeline = session.GetTimelineProperties();
                var currentPosition = EstimateCurrentPosition(
                    timeline,
                    playbackInfo.PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                succeeded = await QqMusicSeekFallbackService.TrySeekAsync(
                    currentPosition,
                    clampedPosition);
                usedQqMusicFallback = succeeded;
            }

            if (!succeeded)
            {
                StatusChanged?.Invoke("当前播放器不支持进度跳转");
                return false;
            }

            StatusChanged?.Invoke(
                usedQqMusicFallback
                    ? "已通过 QQ 音乐调整播放位置"
                    : "已调整播放位置");
            await Task.Delay(
                usedQqMusicFallback
                    ? TimeSpan.FromMilliseconds(1150)
                    : TimeSpan.FromMilliseconds(450));
            await RefreshSnapshotAsync();
            return true;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"进度跳转失败：{exception.Message}");
            return false;
        }
    }

    private static TimeSpan EstimateCurrentPosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool isPlaying)
    {
        var position = timeline.Position;
        if (isPlaying && timeline.LastUpdatedTime != default)
        {
            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero
                && elapsed < TimeSpan.FromMinutes(5))
            {
                position += elapsed;
            }
        }

        if (position < timeline.StartTime)
        {
            position = timeline.StartTime;
        }

        if (timeline.EndTime > timeline.StartTime
            && position > timeline.EndTime)
        {
            position = timeline.EndTime;
        }

        return position;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachCurrentSession();

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        BindCurrentSession();
        _ = RefreshSnapshotAsync();
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        BindCurrentSession();
        _ = RefreshSnapshotAsync();
    }

    private void BindCurrentSession()
    {
        var nextSession = _manager?.GetCurrentSession();

        DetachCurrentSession();
        _currentSession = nextSession;

        if (_currentSession is null)
        {
            return;
        }

        _currentSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
        _currentSession.TimelinePropertiesChanged += OnSessionTimelinePropertiesChanged;
    }

    private void DetachCurrentSession()
    {
        if (_currentSession is null)
        {
            return;
        }

        _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
        _currentSession.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
        _currentSession = null;
    }

    private void OnSessionMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshSnapshotAsync();
    }

    private void OnSessionPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshSnapshotAsync();
    }

    private void OnSessionTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        _ = RefreshSnapshotAsync();
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshGate.WaitAsync();

        try
        {
            var session = _currentSession;
            if (session is null)
            {
                StatusChanged?.Invoke("未检测到活动媒体");
                SnapshotChanged?.Invoke(null);
                return;
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            if (_disposed || !ReferenceEquals(session, _currentSession))
            {
                return;
            }

            var playbackInfo = session.GetPlaybackInfo();
            var controls = playbackInfo.Controls;
            var timeline = session.GetTimelineProperties();

            var title = string.IsNullOrWhiteSpace(mediaProperties.Title)
                ? "未知媒体"
                : mediaProperties.Title.Trim();

            var artist = FirstNonEmpty(
                mediaProperties.Artist,
                mediaProperties.AlbumArtist,
                GetSourceDisplayName(session.SourceAppUserModelId));

            var lyricLine = string.IsNullOrWhiteSpace(mediaProperties.Subtitle)
                ? null
                : mediaProperties.Subtitle.Trim();
            var albumTitle = mediaProperties.AlbumTitle?.Trim() ?? string.Empty;
            var artworkIdentity = string.Join(
                '\u001F',
                session.SourceAppUserModelId,
                title,
                artist,
                albumTitle);
            byte[]? artwork;
            if (string.Equals(
                    artworkIdentity,
                    _artworkIdentity,
                    StringComparison.Ordinal)
                && _cachedArtwork is not null)
            {
                artwork = _cachedArtwork;
            }
            else
            {
                artwork = await ReadArtworkAsync(mediaProperties.Thumbnail);
                if (_disposed || !ReferenceEquals(session, _currentSession))
                {
                    return;
                }

                _artworkIdentity = artworkIdentity;
                _cachedArtwork = artwork;
            }

            var snapshot = new MediaSnapshot(
                session.SourceAppUserModelId,
                GetSourceDisplayName(session.SourceAppUserModelId),
                title,
                artist,
                albumTitle,
                lyricLine,
                playbackInfo.PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls.IsPlayEnabled,
                controls.IsPauseEnabled,
                controls.IsPreviousEnabled,
                controls.IsNextEnabled,
                timeline.Position,
                timeline.EndTime,
                timeline.LastUpdatedTime,
                artwork);

            StatusChanged?.Invoke(snapshot.IsPlaying ? "正在播放" : "媒体已暂停");
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"读取媒体失败：{exception.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> ExecuteControlAsync(
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        var session = _currentSession;
        if (_disposed || session is null)
        {
            return false;
        }

        try
        {
            var succeeded = await action(session);
            if (succeeded)
            {
                await RefreshSnapshotAsync();
            }

            return succeeded;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"媒体控制失败：{exception.Message}");
            return false;
        }
    }

    private static async Task<byte[]?> ReadArtworkAsync(
        IRandomAccessStreamReference? artworkReference)
    {
        if (artworkReference is null)
        {
            return null;
        }

        try
        {
            using var stream = await artworkReference.OpenReadAsync();
            if (stream.Size > MaximumArtworkBytes)
            {
                return null;
            }

            // Some Win32 media players expose a valid thumbnail stream while
            // reporting Size == 0. Reading in partial chunks avoids discarding
            // that cover while still enforcing a strict memory limit.
            using var destination = stream.Size > 0
                ? new MemoryStream(checked((int)stream.Size))
                : new MemoryStream();
            using var reader = new DataReader(stream.GetInputStreamAt(0))
            {
                InputStreamOptions = InputStreamOptions.Partial
            };
            const uint chunkSize = 32 * 1024;
            while (true)
            {
                var loaded = await reader.LoadAsync(chunkSize);
                if (loaded == 0)
                {
                    break;
                }

                if (destination.Length + loaded > MaximumArtworkBytes)
                {
                    return null;
                }

                var buffer = new byte[loaded];
                reader.ReadBytes(buffer);
                destination.Write(buffer, 0, buffer.Length);
            }

            return destination.Length == 0
                ? null
                : destination.ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or COMException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
               ?? "未知来源";
    }

    private static string GetSourceDisplayName(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return "媒体应用";
        }

        var source = sourceAppUserModelId;
        var separatorIndex = source.IndexOf('!');
        if (separatorIndex > 0)
        {
            source = source[..separatorIndex];
        }

        if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (source.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase))
        {
            return "Media Player";
        }

        if (source.Contains("QQMusic", StringComparison.OrdinalIgnoreCase))
        {
            return "QQ 音乐";
        }

        if (source.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (source.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)
            || source.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Edge";
        }

        var segments = source.Split(
            ['.', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.LastOrDefault() ?? source;
    }
}
