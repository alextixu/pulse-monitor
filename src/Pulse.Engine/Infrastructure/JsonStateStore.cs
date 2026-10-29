using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Ipc;
using Pulse.Core.Models;

namespace Pulse.Engine;

/// <summary>state.json in %LOCALAPPDATA%\Pulse: atomic writes (temp file + move); a corrupt file is kept as .corrupt and replaced by an empty state.</summary>
public sealed class JsonStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly ILogger<JsonStateStore> _log;

    public JsonStateStore(string filePath, ILogger<JsonStateStore> log)
    {
        FilePath = filePath;
        _log = log;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "state.json");

    public string FilePath { get; }

    private static JsonTypeInfo<PulseState> FileTypeInfo => (JsonTypeInfo<PulseState>)IpcJsonContext.FileOptions.GetTypeInfo(typeof(PulseState));

    /// <summary>The stored state; an empty one when the file is missing or unreadable (never throws).</summary>
    public PulseState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    _log.LogInformation("State file {Path} not found; starting with an empty memory", FilePath);
                    return new PulseState();
                }

                var state = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), IpcJsonContext.Default.PulseState) ?? new PulseState();
                if (state.Version > PulseState.CurrentVersion)
                {
                    // Written by a newer Pulse: read what we understand (additive schema), never downgrade the file silently.
                    _log.LogInformation("State file {Path} has version {Version} (this build knows {Known})", FilePath, state.Version, PulseState.CurrentVersion);
                }
                _log.LogDebug("State loaded from {Path}: {Batteries} batteries, {Lighting} lighting intents, {Fans} fans",
                    FilePath, state.Batteries.Count, state.Lighting.Count, state.Fans.Count);
                return Repair(state);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "State file {Path} is unreadable; starting with an empty memory", FilePath);
                TryBackupCorruptFile();
                return new PulseState();
            }
        }
    }

    /// <summary>Atomic replace; failures are logged, never thrown (the next debounced save retries).</summary>
    public void Save(PulseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            string? temp = null;
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                temp = Path.Combine(dir, $"state.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state with { Version = PulseState.CurrentVersion }, FileTypeInfo));
                File.Move(temp, FilePath, overwrite: true);
                _log.LogDebug("State saved to {Path}", FilePath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Saving state to {Path} failed", FilePath);
            }
            finally
            {
                try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Null collections from a hand-edited file become empty ones (the records promise non-null lists).</summary>
    private static PulseState Repair(PulseState state) => state with
    {
        Batteries = (state.Batteries ?? Array.Empty<RememberedBattery>()).Where(b => b?.Device?.Id is not null).ToList(),
        Lighting = state.Lighting ?? new Dictionary<string, LightingIntent>(),
        Fans = (state.Fans ?? Array.Empty<FanMemory>()).Where(f => f?.FanId is not null).ToList(),
        LowBatteryNotified = state.LowBatteryNotified ?? Array.Empty<string>(),
    };

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = FilePath + ".corrupt";
            File.Copy(FilePath, backup, overwrite: true);
            File.Delete(FilePath);
            _log.LogInformation("Corrupt state copied to {Backup}", backup);
        }
        catch
        {
            // Best effort only.
        }
    }
}
