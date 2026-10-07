using Pulse.Core.Abstractions;
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
