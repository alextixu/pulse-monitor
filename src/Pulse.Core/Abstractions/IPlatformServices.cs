using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>Process elevation (Windows UAC / sudo). Needed for CPU temperature and fan control on Windows.</summary>
public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>True when the platform supports re-launching the app with higher privileges.</summary>
    bool CanElevate { get; }

    /// <summary>
    /// Starts a new elevated instance of this application. Returns true if the new process was started
    /// (the caller should then shut the current instance down). Returns false if the user cancelled or it failed.
    /// </summary>
    bool RelaunchElevated();
}

/// <summary>Launch-at-login registration.</summary>
public interface IAutoStartService
{
    bool IsSupported { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public interface ISettingsStore
{
    string FilePath { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class NullElevationService : IElevationService
{
    public bool IsElevated => false;
    public bool CanElevate => false;
    public bool RelaunchElevated() => false;
}

public sealed class NullAutoStartService : IAutoStartService
{
    public bool IsSupported => false;
    public bool IsEnabled => false;
    public void SetEnabled(bool enabled) { }
}
