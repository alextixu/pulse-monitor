using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Core.Tests.Fakes;

/// <summary>
/// Records every <see cref="SetFanAsync"/> call in order (including the ones it was told to fail) and can be made
/// to throw for a given fan id. <see cref="OnSet"/> runs before the call is recorded, so a test can react mid-flight
/// (e.g. call <c>Stop()</c> on the controller while an evaluation is still writing).
/// </summary>
public sealed class FakeHardwareMonitor : IHardwareMonitor
{
    public List<(string Id, double? Percent)> Calls { get; } = new();

    /// <summary>Ids whose <see cref="SetFanAsync"/> throws (after the attempt was recorded).</summary>
    public HashSet<string> FailingIds { get; } = new(StringComparer.Ordinal);

    public Func<string, double?, Task>? OnSet { get; set; }

    public HardwareMonitorStatus Status { get; set; } = new();

    public event EventHandler<HardwareMonitorStatus>? StatusChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(HardwareSnapshot.Empty);

    public async Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
    {
        if (OnSet is not null) await OnSet(fanId, percent).ConfigureAwait(false);
        Calls.Add((fanId, percent));
        if (FailingIds.Contains(fanId)) throw new IOException($"SetFan({fanId}) failed");
    }

    public void Dispose() { }

    public void RaiseStatusChanged() => StatusChanged?.Invoke(this, Status);

    /// <summary>Ids handed back to the firmware (percent == null), in call order.</summary>
    public List<string> HandBacks => Calls.Where(c => c.Percent is null).Select(c => c.Id).ToList();

    /// <summary>Duty writes (percent != null), in call order.</summary>
    public List<(string Id, double Percent)> Writes
        => Calls.Where(c => c.Percent is not null).Select(c => (c.Id, c.Percent!.Value)).ToList();

    public List<(string Id, double? Percent)> CallsFor(string id) => Calls.Where(c => c.Id == id).ToList();

    public void Clear() => Calls.Clear();
}
