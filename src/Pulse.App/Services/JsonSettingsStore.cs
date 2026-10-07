using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Persists <see cref="AppSettings"/> as indented JSON in %APPDATA%\Pulse\settings.json.
/// Missing or corrupt files yield defaults; writes go to a temp file first and are moved into place.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly ILogger<JsonSettingsStore> _log;

    public JsonSettingsStore(ILogger<JsonSettingsStore> log)
        : this(log, DefaultPath)
    {
    }

    public JsonSettingsStore(ILogger<JsonSettingsStore> log, string filePath)
    {
        _log = log;
        FilePath = filePath;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulse", "settings.json");

    public string FilePath { get; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    _log.LogInformation("Settings file {Path} not found; using defaults", FilePath);
                    return new AppSettings();
                }

                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                Sanitize(settings);
                _log.LogDebug("Settings loaded from {Path}", FilePath);
                return settings;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Settings file {Path} is unreadable; using defaults", FilePath);
                TryBackupCorruptFile();
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Sanitize(settings);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, FilePath, overwrite: true);
                _log.LogDebug("Settings saved to {Path}", FilePath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private static void Sanitize(AppSettings s)
    {
        s.HardwareRefreshSeconds = Math.Clamp(s.HardwareRefreshSeconds, 1, 3600);
        s.BatteryRefreshSeconds = Math.Clamp(s.BatteryRefreshSeconds, 5, 86400);
        s.OpenRgbPort = Math.Clamp(s.OpenRgbPort, 1, 65535);
        s.LowBatteryThresholdPercent = Math.Clamp(s.LowBatteryThresholdPercent, 1, 99);
        s.OpenRgbHost = string.IsNullOrWhiteSpace(s.OpenRgbHost) ? "127.0.0.1" : s.OpenRgbHost.Trim();
        s.HiddenDeviceIds ??= new List<string>();
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = FilePath + ".corrupt";
            File.Copy(FilePath, backup, overwrite: true);
            _log.LogInformation("Corrupt settings copied to {Backup}", backup);
        }
        catch
        {
            // Best effort only.
        }
    }
}
