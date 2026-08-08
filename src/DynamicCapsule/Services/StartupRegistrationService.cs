using DynamicCapsule.Models;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Windows.ApplicationModel;

namespace DynamicCapsule.Services;

internal sealed class StartupRegistrationService
{
    private const string StartupTaskId = "DynamicCapsuleStartup";
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Windows Dynamic Capsule";
    private const int AppModelErrorNoPackage = 15700;

    private readonly bool _hasPackageIdentity;
    private readonly string? _startupScriptPath;
    private readonly string _configuration;
    private readonly string _expectedCommand;

    internal StartupRegistrationService()
    {
        _hasPackageIdentity = HasPackageIdentity();
        _startupScriptPath = FindStartupScript();
        _configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        _expectedCommand = _startupScriptPath is null
            ? string.Empty
            : BuildCommand(_startupScriptPath, _configuration);
    }

    internal StartupRegistrationStatus GetStatus()
    {
        if (_hasPackageIdentity)
        {
            return GetPackagedStatus();
        }

        return GetUnpackagedStatus();
    }

    internal async Task<(bool Success, string? Error)> TrySetEnabledAsync(
        bool isEnabled)
    {
        if (_hasPackageIdentity)
        {
            return await TrySetPackagedEnabledAsync(isEnabled);
        }

        return TrySetUnpackagedEnabled(isEnabled);
    }

    private StartupRegistrationStatus GetPackagedStatus()
    {
        try
        {
            var startupTask = StartupTask.GetAsync(StartupTaskId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return ConvertPackagedStatus(startupTask.State);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or UnauthorizedAccessException
            or COMException)
        {
            return new StartupRegistrationStatus(
                false,
                false,
                $"无法读取 MSIX 登录启动状态：{exception.Message}");
        }
    }

    private static StartupRegistrationStatus ConvertPackagedStatus(
        StartupTaskState state)
    {
        return state switch
        {
            StartupTaskState.Enabled =>
                new StartupRegistrationStatus(
                    true,
                    true,
                    "登录启动已启用（MSIX）"),
            StartupTaskState.EnabledByPolicy =>
                new StartupRegistrationStatus(
                    true,
                    false,
                    "登录启动已由系统策略启用"),
            StartupTaskState.Disabled =>
                new StartupRegistrationStatus(
                    false,
                    true,
                    "登录启动未启用（MSIX）"),
            StartupTaskState.DisabledByUser =>
                new StartupRegistrationStatus(
                    false,
                    true,
                    "登录启动已在任务管理器中禁用"),
            StartupTaskState.DisabledByPolicy =>
                new StartupRegistrationStatus(
                    false,
                    false,
                    "登录启动已被系统策略禁用"),
            _ =>
                new StartupRegistrationStatus(
                    false,
                    false,
                    $"未知的 MSIX 登录启动状态：{state}")
        };
    }

    private static async Task<(bool Success, string? Error)>
        TrySetPackagedEnabledAsync(bool isEnabled)
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (!isEnabled)
            {
                if (startupTask.State == StartupTaskState.EnabledByPolicy)
                {
                    return (
                        false,
                        "登录启动已由系统策略启用，应用无法将其关闭。");
                }

                startupTask.Disable();
                return (true, null);
            }

            if (startupTask.State is StartupTaskState.Enabled
                or StartupTaskState.EnabledByPolicy)
            {
                return (true, null);
            }

            if (startupTask.State == StartupTaskState.DisabledByUser)
            {
                return (
                    false,
                    "登录启动已在任务管理器中禁用，请在 Windows“启动应用”设置中重新启用。");
            }

            if (startupTask.State == StartupTaskState.DisabledByPolicy)
            {
                return (
                    false,
                    "登录启动已被系统策略禁用。");
            }

            var result = await startupTask.RequestEnableAsync();
            return result switch
            {
                StartupTaskState.Enabled
                    or StartupTaskState.EnabledByPolicy =>
                    (true, null),
                StartupTaskState.DisabledByUser =>
                    (
                        false,
                        "登录启动已在任务管理器中禁用，请在 Windows“启动应用”设置中重新启用。"),
                StartupTaskState.DisabledByPolicy =>
                    (false, "登录启动已被系统策略禁用。"),
                _ => (false, $"Windows 未启用登录启动：{result}")
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or UnauthorizedAccessException
            or COMException)
        {
            return (
                false,
                $"无法更新 MSIX 登录启动设置：{exception.Message}");
        }
    }

    private StartupRegistrationStatus GetUnpackagedStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                RunKeyPath,
                writable: false);
            var currentCommand = key?.GetValue(
                ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

            if (string.IsNullOrWhiteSpace(currentCommand))
            {
                return new StartupRegistrationStatus(
                    false,
                    true,
                    "登录启动未启用");
            }

            if (!string.IsNullOrWhiteSpace(_expectedCommand)
                && string.Equals(
                    currentCommand,
                    _expectedCommand,
                    StringComparison.Ordinal))
            {
                return new StartupRegistrationStatus(
                    true,
                    true,
                    $"登录启动已启用（{_configuration}）");
            }

            return new StartupRegistrationStatus(
                false,
                false,
                "检测到不匹配的旧启动项，需要重新启用");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or SecurityException
            or IOException)
        {
            return new StartupRegistrationStatus(
                false,
                false,
                $"无法读取登录启动状态：{exception.Message}");
        }
    }

    private (bool Success, string? Error) TrySetUnpackagedEnabled(
        bool isEnabled)
    {
        try
        {
            if (isEnabled)
            {
                if (_startupScriptPath is null
                    || !File.Exists(_startupScriptPath))
                {
                    return (
                        false,
                        "找不到项目启动脚本，无法启用登录启动。");
                }

                using var key = Registry.CurrentUser.CreateSubKey(
                    RunKeyPath,
                    writable: true);
                if (key is null)
                {
                    return (
                        false,
                        "无法打开当前用户的登录启动注册表项。");
                }

                key.SetValue(
                    ValueName,
                    _expectedCommand,
                    RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    RunKeyPath,
                    writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            var status = GetStatus();
            if (status.IsValid && status.IsEnabled == isEnabled)
            {
                return (true, null);
            }

            return (false, status.Message);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or SecurityException
            or IOException)
        {
            return (false, exception.Message);
        }
    }

    private static bool HasPackageIdentity()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);

    private static string? FindStartupScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "scripts",
                "start-dynamic-capsule.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string BuildCommand(
        string scriptPath,
        string configuration)
    {
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return $"\"{powershellPath}\" -NoProfile -WindowStyle Hidden "
               + "-ExecutionPolicy Bypass "
               + $"-File \"{scriptPath}\" -Configuration {configuration}";
    }
}
