using DynamicCapsule.Models;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DynamicCapsule.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private const int MaximumCommandCharacters = 8 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly bool _ownsMutex;

    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;
    private bool _disposed;

    internal SingleInstanceService()
    {
        var userScope = CreateUserScope();
        var mutexName =
            $@"Local\WindowsDynamicCapsule.SingleInstance.{userScope}.v1";
        _pipeName =
            $"WindowsDynamicCapsule.Command.{userScope}.v1";
        _mutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var createdNew);
        _ownsMutex = createdNew;
        IsPrimaryInstance = createdNew;
    }

    internal bool IsPrimaryInstance { get; }

    internal event Action<SingleInstanceCommand>? CommandReceived;

    internal void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimaryInstance || _listenTask is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _listenTask = ListenAsync(_cancellation.Token);
    }

    internal async Task<bool> ForwardToPrimaryAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimaryInstance)
        {
            return false;
        }

        var command = new SingleInstanceCommand(
            1,
            NormalizeArguments(arguments),
            Truncate(Environment.CurrentDirectory, 512),
            DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(command, SerializerOptions);
        if (json.Length > MaximumCommandCharacters)
        {
            return false;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            await writer
                .WriteLineAsync(json.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await writer
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down; the OS releases the handle.
            }
        }

        _mutex.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                var command = ParseCommand(line);
                if (command is not null)
                {
                    CommandReceived?.Invoke(command);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                try
                {
                    await Task.Delay(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static SingleInstanceCommand? ParseCommand(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || json.Length > MaximumCommandCharacters)
        {
            return null;
        }

        try
        {
            var command = JsonSerializer.Deserialize<SingleInstanceCommand>(
                json,
                SerializerOptions);
            if (command is null
                || command.Version != 1
                || command.Arguments is null
                || command.WorkingDirectory is null
                || command.Arguments.Length > 16
                || command.Arguments.Any(argument =>
                    string.IsNullOrWhiteSpace(argument)
                    || argument.Length > 512)
                || command.WorkingDirectory.Length > 512
                || command.Timestamp < DateTimeOffset.UtcNow.AddMinutes(-5)
                || command.Timestamp > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return null;
            }

            return command with
            {
                Arguments = NormalizeArguments(command.Arguments),
                WorkingDirectory = Truncate(command.WorkingDirectory, 512)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] NormalizeArguments(IEnumerable<string> arguments)
    {
        return arguments
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .Take(16)
            .Select(argument => Truncate(argument.Trim(), 512))
            .ToArray();
    }

    private static string CreateUserScope()
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value
                       ?? Environment.UserName;
        }
        catch
        {
            identity = Environment.UserName;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength];
    }
}
