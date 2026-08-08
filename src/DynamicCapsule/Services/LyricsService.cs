using DynamicCapsule.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicCapsule.Services;

internal sealed class LyricsService : IDisposable
{
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(300);
    private static readonly Regex TimestampPattern = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2}(?:\.\d{1,3})?)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TitleQualifierPattern = new(
        @"\s*[\(\[].*?[\)\]]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, LyricsLookupResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _cacheLock = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRequestNotBefore = DateTimeOffset.MinValue;
    private bool _disposed;

    internal LyricsService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WindowsDynamicCapsule", "0.1"));
        }
    }

    internal async Task<LyricsLookupResult> GetLyricsAsync(
        MediaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cacheKey = BuildCacheKey(snapshot);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var result = await LookupCoreAsync(snapshot, cancellationToken);
            if (result.Status is not LyricsLookupStatus.Unavailable)
            {
                lock (_cacheLock)
                {
                    _cache[cacheKey] = result;
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return LyricsLookupResult.Empty(LyricsLookupStatus.Unavailable);
        }
        catch (TaskCanceledException)
        {
            return LyricsLookupResult.Empty(LyricsLookupStatus.Unavailable);
        }
        catch (JsonException)
        {
            return LyricsLookupResult.Empty(LyricsLookupStatus.Unavailable);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<LyricsLookupResult> LookupCoreAsync(
        MediaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        QqMusicLookupResult? qqMusicResult = null;
        if (IsQqMusicSnapshot(snapshot))
        {
            qqMusicResult = await TryLookupQqMusicAsync(
                snapshot,
                cancellationToken);
            if (qqMusicResult is { Lines.Count: > 0 })
            {
                return new LyricsLookupResult(
                    LyricsLookupStatus.Found,
                    qqMusicResult.Lines,
                    "QQ 音乐")
                {
                    Artwork = qqMusicResult.Artwork,
                    Duration = qqMusicResult.Duration
                };
            }
        }

        var lrcLibResult = await LookupLrcLibAsync(
            snapshot,
            cancellationToken);
        return qqMusicResult?.Artwork is null
            ? lrcLibResult
            : lrcLibResult with { Artwork = qqMusicResult.Artwork };
    }

    private async Task<LyricsLookupResult> LookupLrcLibAsync(
        MediaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        LrcLibRecord? record = null;
        var lookupTitle = StripTitleQualifiers(snapshot.Title);

        if (!string.IsNullOrWhiteSpace(snapshot.AlbumTitle)
            && snapshot.EndTime > TimeSpan.Zero)
        {
            var exactPath = BuildQuery(
                "/api/get",
                ("track_name", lookupTitle),
                ("artist_name", snapshot.Artist),
                ("album_name", snapshot.AlbumTitle),
                ("duration", Math.Round(snapshot.EndTime.TotalSeconds).ToString(
                    CultureInfo.InvariantCulture)));

            record = await GetRecordAsync(exactPath, cancellationToken);
        }

        if (record is null)
        {
            var searchPath = BuildQuery(
                "/api/search",
                ("track_name", lookupTitle),
                ("artist_name", snapshot.Artist));

            var records = await GetRecordsAsync(searchPath, cancellationToken);
            record = SelectBestMatch(records, snapshot);
        }

        return CreateResult(record);
    }

    private async Task<QqMusicLookupResult?> TryLookupQqMusicAsync(
        MediaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = $"{snapshot.Title} {snapshot.Artist}".Trim();
            var searchUri = new Uri(
                "https://c.y.qq.com/soso/fcgi-bin/client_search_cp"
                + "?p=1&n=10&format=json&w="
                + Uri.EscapeDataString(query));
            using var searchResponse = await SendQqMusicAsync(
                searchUri,
                cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            await using var searchStream = await searchResponse.Content
                .ReadAsStreamAsync(cancellationToken);
            var search = await JsonSerializer.DeserializeAsync<QqMusicSearchResponse>(
                searchStream,
                SerializerOptions,
                cancellationToken);
            var selected = SelectBestQqMusicMatch(
                search?.Data?.Song?.List ?? [],
                snapshot);
            if (selected is null || string.IsNullOrWhiteSpace(selected.SongMid))
            {
                return null;
            }

            var artwork = snapshot.Artwork is null
                ? await TryGetQqMusicArtworkAsync(
                    selected.AlbumMid,
                    cancellationToken)
                : null;
            var lines = await TryGetQqMusicLyricsAsync(
                selected.SongMid,
                cancellationToken);
            return new QqMusicLookupResult(
                lines,
                artwork,
                TimeSpan.FromSeconds(Math.Max(0, selected.Interval)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<LyricLine>> TryGetQqMusicLyricsAsync(
        string songMid,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(
                "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
                + "?format=json&nobase64=1&songmid="
                + Uri.EscapeDataString(songMid));
            using var response = await SendQqMusicAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<QqMusicLyricsResponse>(
                stream,
                SerializerOptions,
                cancellationToken);
            return ParseSyncedLyrics(result?.Lyric);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or JsonException)
        {
            return Array.Empty<LyricLine>();
        }
    }

    private async Task<byte[]?> TryGetQqMusicArtworkAsync(
        string? albumMid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumMid))
        {
            return null;
        }

        try
        {
            var uri = new Uri(
                "https://y.gtimg.cn/music/photo_new/"
                + "T002R300x300M000"
                + Uri.EscapeDataString(albumMid)
                + ".jpg");
            using var response = await SendQqMusicAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(
                cancellationToken);
            return bytes.Length is > 0 and <= 4 * 1024 * 1024
                ? bytes
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendQqMusicAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private async Task<LrcLibRecord?> GetRecordAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(requestPath, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LrcLibRecord>(
            stream,
            SerializerOptions,
            cancellationToken);
    }

    private async Task<IReadOnlyList<LrcLibRecord>> GetRecordsAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(requestPath, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<LrcLibRecord>>(
                   stream,
                   SerializerOptions,
                   cancellationToken)
               ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nextAllowedAt = _lastRequestAt + MinimumRequestInterval;
        if (_nextRequestNotBefore > nextAllowedAt)
        {
            nextAllowedAt = _nextRequestNotBefore;
        }

        var waitTime = nextAllowedAt - now;
        if (waitTime > TimeSpan.Zero)
        {
            await Task.Delay(waitTime, cancellationToken);
        }

        _lastRequestAt = DateTimeOffset.UtcNow;
        var response = await _httpClient.GetAsync(
            requestPath,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter;
            _nextRequestNotBefore = retryAfter?.Date
                                    ?? DateTimeOffset.UtcNow
                                    + (retryAfter?.Delta ?? TimeSpan.FromSeconds(30));
        }

        return response;
    }

    private static LyricsLookupResult CreateResult(LrcLibRecord? record)
    {
        if (record is null)
        {
            return LyricsLookupResult.Empty(LyricsLookupStatus.NotFound);
        }

        if (record.Instrumental)
        {
            return LyricsLookupResult.Empty(LyricsLookupStatus.Instrumental)
                with { Duration = TimeSpan.FromSeconds(record.Duration) };
        }

        var lines = ParseSyncedLyrics(record.SyncedLyrics);
        if (lines.Count > 0)
        {
            return new LyricsLookupResult(
                LyricsLookupStatus.Found,
                lines,
                "LRCLIB")
            {
                Duration = TimeSpan.FromSeconds(record.Duration)
            };
        }

        return LyricsLookupResult.Empty(
                string.IsNullOrWhiteSpace(record.PlainLyrics)
                    ? LyricsLookupStatus.NotFound
                    : LyricsLookupStatus.UnsyncedOnly)
            with { Duration = TimeSpan.FromSeconds(record.Duration) };
    }

    internal static IReadOnlyList<LyricLine> ParseSyncedLyrics(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return Array.Empty<LyricLine>();
        }

        var parsedLines = new List<LyricLine>();
        foreach (var rawLine in lyrics.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = TimestampPattern.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = rawLine[(matches[^1].Index + matches[^1].Length)..].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match match in matches)
            {
                if (!int.TryParse(
                        match.Groups["minutes"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var minutes)
                    || !double.TryParse(
                        match.Groups["seconds"].Value,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var seconds))
                {
                    continue;
                }

                parsedLines.Add(new LyricLine(
                    TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds),
                    text));
            }
        }

        return parsedLines
            .OrderBy(line => line.Timestamp)
            .ToArray();
    }

    private static LrcLibRecord? SelectBestMatch(
        IReadOnlyList<LrcLibRecord> records,
        MediaSnapshot snapshot)
    {
        var expectedTitle = NormalizeForMatch(snapshot.Title);
        var expectedArtist = NormalizeForMatch(snapshot.Artist);

        return records
            .Select(record => new
            {
                Record = record,
                Score = ScoreRecord(record, snapshot, expectedTitle, expectedArtist)
            })
            .Where(candidate => candidate.Score >= 12)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs(
                candidate.Record.Duration - snapshot.EndTime.TotalSeconds))
            .Select(candidate => candidate.Record)
            .FirstOrDefault();
    }

    private static QqMusicSong? SelectBestQqMusicMatch(
        IReadOnlyList<QqMusicSong> songs,
        MediaSnapshot snapshot)
    {
        var expectedTitle = NormalizeForMatch(snapshot.Title);
        var expectedArtist = NormalizeForMatch(snapshot.Artist);

        return songs
            .Select(song => new
            {
                Song = song,
                Score = ScoreQqMusicSong(
                    song,
                    snapshot,
                    expectedTitle,
                    expectedArtist)
            })
            .Where(candidate => candidate.Score >= 12)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs(
                candidate.Song.Interval - snapshot.EndTime.TotalSeconds))
            .Select(candidate => candidate.Song)
            .FirstOrDefault();
    }

    private static int ScoreQqMusicSong(
        QqMusicSong song,
        MediaSnapshot snapshot,
        string expectedTitle,
        string expectedArtist)
    {
        var actualTitle = NormalizeForMatch(song.SongName);
        var actualArtist = NormalizeForMatch(string.Join(
            '/',
            (song.Singer ?? []).Select(value => value.Name)));
        var score = actualTitle == expectedTitle
            ? 10
            : actualTitle.Contains(expectedTitle, StringComparison.Ordinal)
              || expectedTitle.Contains(actualTitle, StringComparison.Ordinal)
                ? 4
                : 0;

        if (actualArtist == expectedArtist)
        {
            score += 8;
        }
        else if (actualArtist.Contains(expectedArtist, StringComparison.Ordinal)
                 || expectedArtist.Contains(actualArtist, StringComparison.Ordinal))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AlbumTitle)
            && NormalizeForMatch(song.AlbumName)
            == NormalizeForMatch(snapshot.AlbumTitle))
        {
            score += 3;
        }

        score += Math.Abs(song.Interval - snapshot.EndTime.TotalSeconds) switch
        {
            <= 2 => 4,
            <= 5 => 2,
            _ => 0
        };
        return score;
    }

    private static int ScoreRecord(
        LrcLibRecord record,
        MediaSnapshot snapshot,
        string expectedTitle,
        string expectedArtist)
    {
        var score = 0;
        var actualTitle = NormalizeForMatch(record.TrackName);
        var actualArtist = NormalizeForMatch(record.ArtistName);

        if (actualTitle == expectedTitle)
        {
            score += 10;
        }

        if (actualArtist == expectedArtist)
        {
            score += 8;
        }
        else if (actualArtist.Contains(expectedArtist, StringComparison.Ordinal)
                 || expectedArtist.Contains(actualArtist, StringComparison.Ordinal))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AlbumTitle)
            && NormalizeForMatch(record.AlbumName) == NormalizeForMatch(snapshot.AlbumTitle))
        {
            score += 3;
        }

        var durationDifference = Math.Abs(record.Duration - snapshot.EndTime.TotalSeconds);
        score += durationDifference switch
        {
            <= 2 => 4,
            <= 5 => 2,
            _ => 0
        };

        if (!string.IsNullOrWhiteSpace(record.SyncedLyrics))
        {
            score += 2;
        }

        return score;
    }

    private static string BuildCacheKey(MediaSnapshot snapshot)
    {
        return string.Join(
            '\u001F',
            NormalizeForMatch(snapshot.Title),
            NormalizeForMatch(snapshot.Artist),
            NormalizeForMatch(snapshot.AlbumTitle),
            Math.Round(snapshot.EndTime.TotalSeconds).ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildQuery(
        string path,
        params (string Name, string Value)[] parameters)
    {
        return $"{path}?{string.Join(
            '&',
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"))}";
    }

    private static string StripTitleQualifiers(string value)
    {
        var stripped = TitleQualifierPattern.Replace(value, " ").Trim();
        return string.IsNullOrWhiteSpace(stripped) ? value.Trim() : stripped;
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = StripTitleQualifiers(value).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsQqMusicSnapshot(MediaSnapshot snapshot)
    {
        return snapshot.SourceAppUserModelId.Contains(
                   "QQMusic",
                   StringComparison.OrdinalIgnoreCase)
               || snapshot.SourceDisplayName.Contains(
                   "QQ 音乐",
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record QqMusicLookupResult(
        IReadOnlyList<LyricLine> Lines,
        byte[]? Artwork,
        TimeSpan Duration);

    private sealed record QqMusicSearchResponse(
        QqMusicSearchData? Data);

    private sealed record QqMusicSearchData(
        QqMusicSongContainer? Song);

    private sealed record QqMusicSongContainer(
        IReadOnlyList<QqMusicSong> List);

    private sealed record QqMusicSong(
        string SongMid,
        string SongName,
        string AlbumMid,
        string AlbumName,
        double Interval,
        IReadOnlyList<QqMusicSinger>? Singer);

    private sealed record QqMusicSinger(string Name);

    private sealed record QqMusicLyricsResponse(string? Lyric);

    private sealed record LrcLibRecord(
        long Id,
        string TrackName,
        string ArtistName,
        string AlbumName,
        double Duration,
        bool Instrumental,
        string? PlainLyrics,
        string? SyncedLyrics);
}
