using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Agent;

/// <summary>
/// The host needs one setting (RequestElevationOnStartup) before the engine exists; this hands the instance it read to the
/// engine's first <see cref="Load"/> instead of parsing settings.json twice (and logging "not found" / backing up a corrupt
/// file twice). Every later call goes straight to the inner store.
/// </summary>
internal sealed class PreloadedSettingsStore : ISettingsStore
{
    private readonly ISettingsStore _inner;
    private AppSettings? _preloaded;

    public PreloadedSettingsStore(ISettingsStore inner) => _inner = inner;

    public string FilePath => _inner.FilePath;

    /// <summary>Reads the settings for the host; the same instance is what the engine's first Load returns.</summary>
    public AppSettings Preload()
    {
        var settings = _inner.Load();
        Volatile.Write(ref _preloaded, settings);
        return settings;
    }

    public AppSettings Load() => Interlocked.Exchange(ref _preloaded, null) ?? _inner.Load();

    public void Save(AppSettings settings) => _inner.Save(settings);
}
