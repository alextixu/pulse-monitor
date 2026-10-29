namespace Pulse.Core.Abstractions;

/// <summary>
/// Optional for battery providers: told when Windows reports that devices were added or removed (WM_DEVICECHANGE), e.g.
/// to retry an interface that is backing off right away instead of waiting for its next scheduled attempt.
/// </summary>
public interface IDeviceChangeAware
{
    void OnDevicesChanged();
}
