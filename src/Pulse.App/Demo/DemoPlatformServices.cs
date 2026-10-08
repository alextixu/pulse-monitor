using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>"--demo": pretends elevation is possible but never relaunches, so the admin banner / menu item are visible.</summary>
public sealed class DemoElevationService : IElevationService
{
    private readonly ILogger<DemoElevationService> _log;

    public DemoElevationService(ILogger<DemoElevationService> log) => _log = log;

    public bool IsElevated => false;

    public bool CanElevate => true;

    public bool RelaunchElevated()
    {
        _log.LogInformation("Demo mode: elevation relaunch requested (no-op)");
        return false;
    }
}

/// <summary>"--demo": in-memory auto-start flag.</summary>
public sealed class DemoAutoStartService : IAutoStartService
{
    public bool IsSupported => true;

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

/// <summary>
/// "--demo" / "--screenshot": starts from the user's saved settings (read-only) and keeps every change in memory,
/// so trying fan modes or themes in the demo never alters the real settings file.
/// </summary>
public sealed class DemoSettingsStore : ISettingsStore
{
    private readonly JsonSettingsStore _inner;
    private readonly ILogger<DemoSettingsStore> _log;

    public DemoSettingsStore(ILogger<JsonSettingsStore> innerLog, ILogger<DemoSettingsStore> log)
    {
        _inner = new JsonSettingsStore(innerLog);
        _log = log;
    }

    public string FilePath => _inner.FilePath;

    public AppSettings Load() => _inner.Load();

    public void Save(AppSettings settings) => _log.LogDebug("Demo mode: settings kept in memory (not saved)");
}
