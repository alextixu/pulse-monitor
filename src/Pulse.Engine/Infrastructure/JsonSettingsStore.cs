using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Pulse.Core.Abstractions;
using Pulse.Core.Ipc;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.Engine;

/// <summary>
/// Persists <see cref="AppSettings"/> as indented JSON in %APPDATA%\Pulse\settings.json.
/// Missing or corrupt files yield defaults; writes go to a temp file first and are moved into place. Uses the
/// source-generated <see cref="IpcJsonContext"/> (same shape as before: property names as declared, enums as strings,
/// comments and trailing commas accepted).
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static JsonTypeInfo<AppSettings> FileTypeInfo => (JsonTypeInfo<AppSettings>)IpcJsonContext.FileOptions.GetTypeInfo(typeof(AppSettings));

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
                var settings = JsonSerializer.Deserialize(json, IpcJsonContext.Default.AppSettings) ?? new AppSettings();
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
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, FileTypeInfo));
                File.Move(temp, FilePath, overwrite: true);
                _log.LogDebug("Settings saved to {Path}", FilePath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private static void Sanitize(AppSettings s) => s.Sanitize();

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
