using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Pulse.Rgb;

/// <summary>
/// State of one OpenRGB device as first seen by Pulse: what "還原預設" puts back. Colours are "#RRGGBB" strings so the
/// file stays readable.
/// </summary>
public sealed record RgbDeviceDefault
{
    public string Key { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public int ModeIndex { get; init; }
    public string ModeName { get; init; } = string.Empty;
    /// <summary>True when the mode writes per-LED colours (Direct / Custom); <see cref="LedColors"/> are then restored too.</summary>
    public bool ModeIsPerLed { get; init; }
    public uint? Speed { get; init; }
    public int? Direction { get; init; }
    public int ColorMode { get; init; }
    public List<string> ModeColors { get; init; } = new();
    public List<string> LedColors { get; init; } = new();
}

/// <summary>
/// Persists <see cref="RgbDeviceDefault"/> snapshots as JSON (default %APPDATA%\Pulse\rgb-defaults.json). A missing or
/// corrupt file yields an empty store (the corrupt file is kept as <c>.corrupt</c>); writes go to a temp file that is
/// moved into place. Thread-safe.
/// </summary>
public sealed class RgbDefaultsStore
{
    private const int FileVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private Dictionary<string, RgbDeviceDefault>? _entries;

    public RgbDefaultsStore(string? filePath, ILogger logger)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath;
        _logger = logger;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulse", "rgb-defaults.json");

    public string FilePath { get; }

    public bool Contains(string key)
    {
        lock (_gate) return Entries.ContainsKey(key);
    }

    public RgbDeviceDefault? Get(string key)
    {
        lock (_gate) return Entries.GetValueOrDefault(key);
    }

    /// <summary>Adds the snapshots whose key is not stored yet; saves once if anything was added. Returns the number added.</summary>
    public int AddMissing(IEnumerable<RgbDeviceDefault> snapshots)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var s in snapshots)
            {
                if (Entries.TryAdd(s.Key, s)) added++;
            }

            if (added > 0) SaveLocked();
            return added;
        }
    }

    /// <summary>Stores <paramref name="snapshot"/>, replacing any previous one for the same key, and saves.</summary>
    public void Set(RgbDeviceDefault snapshot)
    {
        lock (_gate)
        {
            Entries[snapshot.Key] = snapshot;
            SaveLocked();
        }
    }

    private Dictionary<string, RgbDeviceDefault> Entries => _entries ??= Load();

    private Dictionary<string, RgbDeviceDefault> Load()
    {
        var result = new Dictionary<string, RgbDeviceDefault>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath)) return result;
            var file = JsonSerializer.Deserialize<DefaultsFile>(File.ReadAllText(FilePath), Options);
            foreach (var entry in file?.Devices ?? [])
            {
                if (entry is not null && !string.IsNullOrEmpty(entry.Key)) result[entry.Key] = entry;
            }

            _logger.LogDebug("Loaded {Count} RGB default snapshots from {Path}", result.Count, FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RGB defaults file {Path} is unreadable; starting empty", FilePath);
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { /* best effort */ }
            result.Clear();
        }

        return result;
    }

    private void SaveLocked()
    {
        var dir = Path.GetDirectoryName(FilePath);
        var temp = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var file = new DefaultsFile { Version = FileVersion, Devices = Entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList() };
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, FilePath, overwrite: true);
            _logger.LogDebug("Saved {Count} RGB default snapshots to {Path}", file.Devices.Count, FilePath);
        }
        catch (Exception ex)
        {
            // Keep the in-memory copy; the next save retries.
            _logger.LogWarning(ex, "Could not save RGB defaults to {Path}", FilePath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private sealed class DefaultsFile
    {
        public int Version { get; set; }
        public List<RgbDeviceDefault> Devices { get; set; } = new();
    }
}
