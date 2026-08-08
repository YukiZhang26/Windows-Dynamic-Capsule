using DynamicCapsule.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicCapsule.Services;

internal sealed record SettingsLoadResult(
    AppSettings Settings,
    bool UsedDefaults,
    string? Warning);

internal sealed class SettingsService
{
    private const long MaximumSettingsBytes = 64 * 1024;
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly string _settingsPath;

    internal SettingsService(string? settingsDirectory = null)
    {
        settingsDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsDynamicCapsule");
        _settingsPath = Path.Combine(settingsDirectory, SettingsFileName);
    }

    internal string SettingsPath => _settingsPath;

    internal SettingsLoadResult Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsLoadResult(new AppSettings(), true, null);
        }

        try
        {
            var fileInfo = new FileInfo(_settingsPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumSettingsBytes)
            {
                return UseDefaults("设置文件为空或超过 64 KB，已恢复默认设置。");
            }

            var json = File.ReadAllText(_settingsPath);
            using var document = JsonDocument.Parse(json);
            if (!HasRequiredProperties(document.RootElement))
            {
                return UseDefaults("设置文件缺少必要字段，已恢复默认设置。");
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                json,
                SerializerOptions);
            if (settings is null)
            {
                return UseDefaults("设置文件没有有效内容，已恢复默认设置。");
            }

            if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                return UseDefaults(
                    $"不支持设置版本 {settings.SchemaVersion}，已恢复默认设置。");
            }

            return new SettingsLoadResult(settings.Normalize(), false, null);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return UseDefaults($"设置文件无法读取，已恢复默认设置：{exception.Message}");
        }
    }

    internal bool TrySave(AppSettings settings, out string? error)
    {
        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(_settingsPath);
        var temporaryPath = $"{_settingsPath}.tmp";

        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("设置目录无效。");
            }

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A stale temporary file is harmless and can be replaced next time.
            }
        }
    }

    private static SettingsLoadResult UseDefaults(string warning)
    {
        return new SettingsLoadResult(new AppSettings(), true, warning);
    }

    private static bool HasRequiredProperties(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("schemaVersion", out _)
               && root.TryGetProperty("monitorTarget", out _)
               && root.TryGetProperty("topGap", out _)
               && root.TryGetProperty("enableAnimations", out _)
               && root.TryGetProperty("hideInFullscreen", out _)
               && root.TryGetProperty("doNotDisturb", out _)
               && root.TryGetProperty("startWithWindows", out _)
               && root.TryGetProperty("privacyLevel", out _)
               && root.TryGetProperty("notificationAllowList", out _)
               && root.TryGetProperty("notificationBlockList", out _);
    }
}
