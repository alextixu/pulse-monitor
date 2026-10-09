using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>
/// Refreshes every hardware node (including sub-hardware). Per-hardware failures are logged and skipped.
/// Storage is special: SMART queries are slow and can spin up a sleeping hard disk, so drives are refreshed at most every
/// <see cref="StorageInterval"/> and drives matched by <paramref name="skipStorage"/> (hard disks) are never queried.
/// </summary>
internal sealed class UpdateVisitor : IVisitor
{
    public static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly Func<IHardware, bool>? _skipStorage;
    private readonly Dictionary<string, long> _storageUpdatedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedLogged = new(StringComparer.Ordinal);

    public UpdateVisitor(ILogger logger, Func<IHardware, bool>? skipStorage = null)
    {
        _logger = logger;
        _skipStorage = skipStorage;
    }

    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        if (hardware.HardwareType == HardwareType.Storage && !ShouldUpdateStorage(hardware)) return;

        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update failed for hardware {Hardware} ({Identifier})", hardware.Name, hardware.Identifier);
        }

        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(this);
        }
    }

    private bool ShouldUpdateStorage(IHardware drive)
    {
        var id = drive.Identifier.ToString();
        if (_skipStorage?.Invoke(drive) == true)
        {
            if (_skippedLogged.Add(id)) _logger.LogInformation("Not reading {Drive} ({Identifier}): hard disk, SMART queries could spin it up", drive.Name, id);
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        if (_storageUpdatedAt.TryGetValue(id, out var last) && Stopwatch.GetElapsedTime(last, now) < StorageInterval) return false;
        _storageUpdatedAt[id] = now;
        return true;
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}
