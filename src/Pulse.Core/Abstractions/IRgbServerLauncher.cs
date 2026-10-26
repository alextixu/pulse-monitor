namespace Pulse.Core.Abstractions;

/// <summary>Outcome of <see cref="IRgbServerLauncher.EnsureRunningAsync"/>.</summary>
public enum RgbLaunchResult
{
    /// <summary>Something already answers on the SDK port (the user's own OpenRGB, or one started earlier).</summary>
    AlreadyRunning,
    /// <summary>The bundled server was started and its port is open.</summary>
    Started,
    /// <summary>No bundled server ships with this build.</summary>
    NotBundled,
    /// <summary>The configured host is not this machine.</summary>
    NotLocal,
    /// <summary>The bundled server could not be started or never opened its port.</summary>
    Failed,
}

/// <summary>Starts the RGB server shipped with Pulse when nothing answers on the SDK port; stops it again on dispose.</summary>
public interface IRgbServerLauncher : IDisposable
{
    /// <summary>True when a bundled server is present next to the executable.</summary>
    bool IsBundled { get; }

    Task<RgbLaunchResult> EnsureRunningAsync(string host, int port, CancellationToken cancellationToken = default);
}

public sealed class NullRgbServerLauncher : IRgbServerLauncher
{
    public bool IsBundled => false;

    public Task<RgbLaunchResult> EnsureRunningAsync(string host, int port, CancellationToken cancellationToken = default)
        => Task.FromResult(RgbLaunchResult.NotBundled);

    public void Dispose() { }
}
