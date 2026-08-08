using DynamicCapsule.Models;
using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicCapsule.Services;

internal sealed class BrowserDownloadProgressService : IDisposable
{
    internal const string SourceId = "system.browser-downloads";

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan StaleDownloadAge =
        TimeSpan.FromMinutes(5);
    private static readonly string[] PartialExtensions =
    [
        ".crdownload",
        ".part",
        ".partial",
        ".download"
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ObservedDownload> _downloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _directories;
    private readonly System.Threading.Timer _timer;

    private int _scanInProgress;
    private bool _started;
    private bool _disposed;

    internal BrowserDownloadProgressService(
        IEnumerable<string>? directories = null)
    {
        _directories = (directories ?? DiscoverDownloadDirectories())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _timer = new System.Threading.Timer(
            _ => Scan(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal event Action<CapsuleEvent>? EventChanged;
    internal event Action<string>? EventRemoved;

    internal IReadOnlyList<string> MonitoredDirectories => _directories;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    internal void ScanNow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Scan();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        lock (_gate)
        {
            _downloads.Clear();
        }
    }

    internal static string GetTargetPath(string partialPath)
    {
        foreach (var extension in PartialExtensions)
        {
            if (partialPath.EndsWith(
                    extension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return partialPath[..^extension.Length];
            }
        }

        return partialPath;
    }

    internal static string FormatBytes(long byteCount)
    {
        var value = Math.Max(0, byteCount);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var scaled = (double)value;
        var unitIndex = 0;
        while (scaled >= 1024 && unitIndex < units.Length - 1)
        {
            scaled /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value} {units[unitIndex]}"
            : $"{scaled:0.#} {units[unitIndex]}";
    }

    private void Scan()
    {
        if (_disposed
            || Interlocked.Exchange(ref _scanInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var visiblePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var successfullyScannedDirectories = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var changedEvents = new List<CapsuleEvent>();
            var removedEventIds = new List<string>();

            foreach (var directory in _directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly);
                    successfullyScannedDirectories.Add(directory);
                }
                catch
                {
                    continue;
                }

                foreach (var path in files.Where(IsPartialDownloadPath))
                {
                    FileInfo file;
                    try
                    {
                        file = new FileInfo(path);
                        if (!file.Exists
                            || now - file.LastWriteTimeUtc > StaleDownloadAge)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    visiblePaths.Add(file.FullName);
                    UpdateObservation(file, now, changedEvents);
                }
            }

            lock (_gate)
            {
                foreach (var observation in _downloads.Values.ToArray())
                {
                    if (visiblePaths.Contains(observation.PartialPath)
                        || !successfullyScannedDirectories.Contains(
                            observation.DirectoryPath))
                    {
                        continue;
                    }

                    _downloads.Remove(observation.PartialPath);
                    if (TryCreateCompletedEvent(
                            observation,
                            now,
                            out var completedEvent))
                    {
                        changedEvents.Add(completedEvent);
                    }
                    else
                    {
                        removedEventIds.Add(observation.EventId);
                    }
                }
            }

            foreach (var capsuleEvent in changedEvents)
            {
                EventChanged?.Invoke(capsuleEvent);
            }

            foreach (var eventId in removedEventIds)
            {
                EventRemoved?.Invoke(eventId);
            }
        }
        finally
        {
            Volatile.Write(ref _scanInProgress, 0);
        }
    }

    private void UpdateObservation(
        FileInfo file,
        DateTimeOffset now,
        ICollection<CapsuleEvent> changedEvents)
    {
        var targetPath = GetTargetPath(file.FullName);
        var title = Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "浏览器下载";
        }

        var totalBytes = TryGetKnownTotalBytes(targetPath, file.Length);
        lock (_gate)
        {
            if (!_downloads.TryGetValue(file.FullName, out var observation))
            {
                observation = new ObservedDownload(
                    file.FullName,
                    Path.GetDirectoryName(file.FullName) ?? string.Empty,
                    targetPath,
                    CreateEventId(file.FullName),
                    title,
                    file.Length,
                    totalBytes,
                    now);
                _downloads[file.FullName] = observation;
            }
            else
            {
                observation.LastBytes = file.Length;
                observation.TotalBytes = totalBytes;
            }

            if (observation.LastPublishedBytes == file.Length
                && observation.LastPublishedTotalBytes == totalBytes)
            {
                return;
            }

            observation.LastPublishedBytes = file.Length;
            observation.LastPublishedTotalBytes = totalBytes;
            changedEvents.Add(CreateRunningEvent(observation, now));
        }
    }

    private static CapsuleEvent CreateRunningEvent(
        ObservedDownload download,
        DateTimeOffset now)
    {
        double? progress = download.TotalBytes is > 0
            ? Math.Clamp(
                (double)download.LastBytes / download.TotalBytes.Value,
                0,
                1)
            : null;
        var message = download.TotalBytes is > 0
            ? $"{FormatBytes(download.LastBytes)} / "
              + FormatBytes(download.TotalBytes.Value)
            : $"{FormatBytes(download.LastBytes)} · 正在下载";

        return new CapsuleEvent(
            download.EventId,
            CapsuleEventKind.TaskProgress,
            CapsuleEventPriority.Normal,
            SourceId,
            "下载",
            download.Title,
            message,
            progress,
            CapsuleTaskState.Running,
            EventPrivacyLevel.Full,
            download.CreatedAt,
            null);
    }

    private static bool TryCreateCompletedEvent(
        ObservedDownload download,
        DateTimeOffset now,
        out CapsuleEvent capsuleEvent)
    {
        capsuleEvent = default!;
        FileInfo target;
        try
        {
            target = new FileInfo(download.TargetPath);
            if (!target.Exists
                || target.Length < Math.Max(1, download.LastBytes))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        capsuleEvent = new CapsuleEvent(
            download.EventId,
            CapsuleEventKind.TaskProgress,
            CapsuleEventPriority.High,
            SourceId,
            "下载",
            download.Title,
            $"下载完成 · {FormatBytes(target.Length)}",
            1,
            CapsuleTaskState.Succeeded,
            EventPrivacyLevel.Full,
            download.CreatedAt,
            now.AddSeconds(6));
        return true;
    }

    private static long? TryGetKnownTotalBytes(
        string targetPath,
        long receivedBytes)
    {
        try
        {
            var target = new FileInfo(targetPath);
            return target.Exists && target.Length > receivedBytes
                ? target.Length
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPartialDownloadPath(string path)
    {
        return PartialExtensions.Any(extension => path.EndsWith(
            extension,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateEventId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(path).ToUpperInvariant()));
        return $"download:{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static IEnumerable<string> DiscoverDownloadDirectories()
    {
        var directories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            directories.Add(Path.Combine(userProfile, "Downloads"));
        }

        try
        {
            using var shellFolders = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var redirectedDownloads = shellFolders?.GetValue(
                "{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!string.IsNullOrWhiteSpace(redirectedDownloads))
            {
                directories.Add(Environment.ExpandEnvironmentVariables(
                    redirectedDownloads));
            }
        }
        catch
        {
            // The conventional user-profile Downloads directory remains active.
        }

        foreach (var preferencesPath in DiscoverBrowserPreferencesFiles())
        {
            TryAddConfiguredDirectory(preferencesPath, directories);
        }

        return directories;
    }

    private static IEnumerable<string> DiscoverBrowserPreferencesFiles()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        string[] roots =
        [
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")
        ];
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(root)
                    .Where(path => string.Equals(
                                       Path.GetFileName(path),
                                       "Default",
                                       StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(path).StartsWith(
                                       "Profile ",
                                       StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                var preferences = Path.Combine(profile, "Preferences");
                if (File.Exists(preferences))
                {
                    yield return preferences;
                }
            }
        }
    }

    private static void TryAddConfiguredDirectory(
        string preferencesPath,
        ISet<string> directories)
    {
        try
        {
            using var stream = new FileStream(
                preferencesPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty(
                    "download",
                    out var downloadSettings)
                || !downloadSettings.TryGetProperty(
                    "default_directory",
                    out var directoryElement))
            {
                return;
            }

            var directory = directoryElement.GetString();
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(directory);
            }
        }
        catch
        {
            // A locked or malformed browser profile must not affect the capsule.
        }
    }

    private sealed class ObservedDownload(
        string partialPath,
        string directoryPath,
        string targetPath,
        string eventId,
        string title,
        long lastBytes,
        long? totalBytes,
        DateTimeOffset createdAt)
    {
        internal string PartialPath { get; } = partialPath;
        internal string DirectoryPath { get; } = directoryPath;
        internal string TargetPath { get; } = targetPath;
        internal string EventId { get; } = eventId;
        internal string Title { get; } = title;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal long LastBytes { get; set; } = lastBytes;
        internal long? TotalBytes { get; set; } = totalBytes;
        internal long LastPublishedBytes { get; set; } = -1;
        internal long? LastPublishedTotalBytes { get; set; }
    }
}
