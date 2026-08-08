using DynamicCapsule.Models;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicCapsule.Services;

internal sealed class LocalTaskPipeService : IDisposable
{
    internal const string PipeName = "DynamicCapsule.v1";

    private const int MaximumEventCharacters = 16 * 1024;
    private const int MaximumEventsPerConnection = 200;
    private static readonly TimeSpan MinimumUpdateInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, DateTimeOffset> _lastUpdateByEventId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _updateGate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;
    private bool _disposed;

    internal event Action<CapsuleEvent>? EventReceived;
    internal event Action<string>? StatusChanged;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listenTask is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _listenTask = ListenAsync(_cancellation.Token);
        StatusChanged?.Invoke($"本地任务管道已启动：{PipeName}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken);
                await ProcessConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke($"本地任务管道错误：{exception.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        for (var eventCount = 0;
             eventCount < MaximumEventsPerConnection;
             eventCount++)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line.Length > MaximumEventCharacters)
            {
                StatusChanged?.Invoke("已拒绝空事件或超过 16KB 的本地任务事件");
                continue;
            }

            var capsuleEvent = ParseEvent(line);
            if (capsuleEvent is null || ShouldThrottle(capsuleEvent))
            {
                continue;
            }

            EventReceived?.Invoke(capsuleEvent);
        }
    }

    private static CapsuleEvent? ParseEvent(string json)
    {
        TaskEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TaskEventEnvelope>(
                json,
                SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null
            || envelope.Version != 1
            || !IdentifierPattern.IsMatch(envelope.Id ?? string.Empty)
            || !IdentifierPattern.IsMatch(envelope.Source ?? string.Empty)
            || string.IsNullOrWhiteSpace(envelope.Title)
            || envelope.Title.Length > 120
            || (envelope.Message?.Length ?? 0) > 300
            || envelope.Progress is < 0 or > 1)
        {
            return null;
        }

        if (!TryMapType(
                envelope.Type,
                out var taskState,
                out var defaultPriority,
                out var expiresAfter))
        {
            return null;
        }

        var parsedPriority = ParsePriority(envelope.Priority);
        if (!string.IsNullOrWhiteSpace(envelope.Priority)
            && parsedPriority is null)
        {
            return null;
        }

        var priority = parsedPriority ?? defaultPriority;
        var now = DateTimeOffset.UtcNow;
        var createdAt = envelope.Timestamp is { } timestamp
                        && timestamp >= now.AddDays(-1)
                        && timestamp <= now.AddMinutes(5)
            ? timestamp
            : now;

        var progress = taskState is CapsuleTaskState.Succeeded
            ? 1
            : envelope.Progress;
        var source = envelope.Source!;

        return new CapsuleEvent(
            $"task:{source}:{envelope.Id}",
            CapsuleEventKind.TaskProgress,
            priority,
            source,
            source,
            envelope.Title.Trim(),
            (envelope.Message ?? string.Empty).Trim(),
            progress,
            taskState,
            EventPrivacyLevel.Full,
            createdAt,
            expiresAfter is null ? null : now + expiresAfter);
    }

    private bool ShouldThrottle(CapsuleEvent capsuleEvent)
    {
        if (capsuleEvent.TaskState is not CapsuleTaskState.Running)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_updateGate)
        {
            if (_lastUpdateByEventId.TryGetValue(
                    capsuleEvent.EventId,
                    out var lastUpdate)
                && now - lastUpdate < MinimumUpdateInterval)
            {
                return true;
            }

            _lastUpdateByEventId[capsuleEvent.EventId] = now;
            return false;
        }
    }

    private static bool TryMapType(
        string? type,
        out CapsuleTaskState state,
        out CapsuleEventPriority priority,
        out TimeSpan? expiresAfter)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "task.started":
            case "task.progress":
                state = CapsuleTaskState.Running;
                priority = CapsuleEventPriority.Normal;
                expiresAfter = null;
                return true;

            case "task.completed":
                state = CapsuleTaskState.Succeeded;
                priority = CapsuleEventPriority.High;
                expiresAfter = TimeSpan.FromSeconds(6);
                return true;

            case "task.failed":
                state = CapsuleTaskState.Failed;
                priority = CapsuleEventPriority.Critical;
                expiresAfter = TimeSpan.FromSeconds(10);
                return true;

            case "task.cancelled":
                state = CapsuleTaskState.Cancelled;
                priority = CapsuleEventPriority.Normal;
                expiresAfter = TimeSpan.FromSeconds(6);
                return true;

            default:
                state = default;
                priority = default;
                expiresAfter = default;
                return false;
        }
    }

    private static CapsuleEventPriority? ParsePriority(string? priority)
    {
        return priority?.Trim().ToLowerInvariant() switch
        {
            "low" => CapsuleEventPriority.Low,
            "normal" => CapsuleEventPriority.Normal,
            "high" => CapsuleEventPriority.High,
            "critical" => CapsuleEventPriority.Critical,
            null or "" => null,
            _ => null
        };
    }

    private sealed record TaskEventEnvelope(
        int Version,
        string? Type,
        string? Id,
        string? Source,
        string? Title,
        string? Message,
        double? Progress,
        string? Priority,
        DateTimeOffset? Timestamp);
}
