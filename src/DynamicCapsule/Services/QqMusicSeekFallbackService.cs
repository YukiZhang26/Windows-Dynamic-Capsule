using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DynamicCapsule.Services;

internal static class QqMusicSeekFallbackService
{
    private const double PositionToleranceSeconds = 0.75;
    private const int MaximumCommandSeconds = 24 * 60 * 60;

    internal static bool IsQqMusic(string? sourceAppUserModelId)
    {
        return !string.IsNullOrWhiteSpace(sourceAppUserModelId)
               && sourceAppUserModelId.Contains(
                   "QQMusic",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<bool> TrySeekAsync(
        TimeSpan currentPosition,
        TimeSpan targetPosition)
    {
        var command = CreateCommand(currentPosition, targetPosition);
        if (command is null)
        {
            return true;
        }

        var executablePath = FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList =
                {
                    command.Value.Argument,
                    command.Value.Seconds.ToString(
                        CultureInfo.InvariantCulture)
                }
            });
            if (process is null)
            {
                return false;
            }

            // QQ Music forwards the command to its existing hidden command
            // window and normally exits immediately. Do not wait indefinitely
            // if a future version keeps the relay process alive.
            await Task.WhenAny(
                process.WaitForExitAsync(),
                Task.Delay(TimeSpan.FromSeconds(2)));
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    internal static QqMusicSeekCommand? CreateCommand(
        TimeSpan currentPosition,
        TimeSpan targetPosition)
    {
        var deltaSeconds = targetPosition.TotalSeconds
                           - currentPosition.TotalSeconds;
        if (Math.Abs(deltaSeconds) <= PositionToleranceSeconds)
        {
            return null;
        }

        var seconds = Math.Clamp(
            (int)Math.Round(
                Math.Abs(deltaSeconds),
                MidpointRounding.AwayFromZero),
            1,
            MaximumCommandSeconds);
        return new QqMusicSeekCommand(
            deltaSeconds > 0 ? "/forward" : "/rewind",
            seconds);
    }

    private static string? FindExecutablePath()
    {
        Process? bestProcess = null;
        try
        {
            foreach (var process in Process.GetProcessesByName("QQMusic"))
            {
                if (bestProcess is null
                    || bestProcess.MainWindowHandle == IntPtr.Zero
                    && process.MainWindowHandle != IntPtr.Zero)
                {
                    bestProcess?.Dispose();
                    bestProcess = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            var runningPath = bestProcess?.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(runningPath)
                && File.Exists(runningPath))
            {
                return runningPath;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Fall through to the registered application path.
        }
        finally
        {
            bestProcess?.Dispose();
        }

        return ReadRegisteredExecutablePath(
                   Registry.CurrentUser,
                   @"Software\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe")
               ?? ReadRegisteredExecutablePath(
                   Registry.LocalMachine,
                   @"Software\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe")
               ?? ReadRegisteredExecutablePath(
                   Registry.LocalMachine,
                   @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe");
    }

    private static string? ReadRegisteredExecutablePath(
        RegistryKey root,
        string subKeyName)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyName);
            var path = key?.GetValue(null) as string;
            path = path?.Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? path
                : null;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException)
        {
            return null;
        }
    }
}

internal readonly record struct QqMusicSeekCommand(
    string Argument,
    int Seconds);
