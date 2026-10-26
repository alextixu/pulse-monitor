using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>
/// Refreshes every hardware node (including sub-hardware). Per-hardware failures are logged and skipped.
/// Storage is special: SMART queries are slow and can spin up a sleeping hard disk, so drives are refreshed at most every
/// <see cref="SlowInterval"/> and drives matched by <paramref name="skipStorage"/> (hard disks) are never queried.
/// DIMM SPD temperatures (SMBus reads) are refreshed at the same slow rate; the generic memory load still updates every poll.
/// Also turns off LibreHardwareMonitor's per-sensor value history (one day by default, which grows by megabytes per hour
/// at a 1 s poll) and times every node so the cost of a poll can be reported.
/// </summary>
internal sealed partial class UpdateVisitor : IVisitor
{
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly Func<IHardware, bool>? _skipStorage;
    private readonly Dictionary<string, long> _slowUpdatedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedLogged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeTiming> _timings = new(StringComparer.Ordinal);

    public UpdateVisitor(ILogger logger, Func<IHardware, bool>? skipStorage = null)
    {
        _logger = logger;
        _skipStorage = skipStorage;
    }

    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        if (!ShouldUpdate(hardware)) return;

        var started = Stopwatch.GetTimestamp();
        var cpuStarted = ThreadCpu.Now();
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update failed for hardware {Hardware} ({Identifier})", hardware.Name, hardware.Identifier);
        }
        Record(hardware, Stopwatch.GetTimestamp() - started, ThreadCpu.Now() - cpuStarted);

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.ValuesTimeWindow != TimeSpan.Zero) sensor.ValuesTimeWindow = TimeSpan.Zero;
        }

        foreach (var sub in hardware.SubHardware)
        {
            sub.Accept(this);
        }
    }

    /// <summary>
    /// Per-node update time since the last call, slowest first; resets the counters. Wall time includes waiting for the
    /// device; CPU time is what the polling thread actually spent (user + kernel), the part that costs power.
    /// </summary>
    public IReadOnlyList<(string Node, int Count, double AverageMs, double MaxMs, double CpuAverageMs)> TakeTimings()
    {
        var list = _timings
            .Select(kv => (kv.Key, kv.Value.Count,
                kv.Value.Count == 0 ? 0 : Ms(kv.Value.Total) / kv.Value.Count,
                Ms(kv.Value.Max),
                kv.Value.Count == 0 ? 0 : kv.Value.CpuTotal.TotalMilliseconds / kv.Value.Count))
            .OrderByDescending(t => t.Item3)
            .ToList();
        _timings.Clear();
        return list;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    private void Record(IHardware hardware, long ticks, TimeSpan cpu)
    {
        var key = $"{hardware.Identifier} {hardware.Name}";
        if (!_timings.TryGetValue(key, out var timing)) _timings[key] = timing = new NodeTiming();
        timing.Count++;
        timing.Total += ticks;
        timing.CpuTotal += cpu;
        if (ticks > timing.Max) timing.Max = ticks;
    }

    private bool ShouldUpdate(IHardware hardware)
    {
        if (hardware.HardwareType == HardwareType.Storage)
        {
            if (_skipStorage?.Invoke(hardware) == true)
            {
                var id = hardware.Identifier.ToString();
                if (_skippedLogged.Add(id)) _logger.LogInformation("Not reading {Drive} ({Identifier}): hard disk or unknown drive, SMART queries could spin it up", hardware.Name, id);
                return false;
            }
            return IsDue(hardware);
        }

        // The embedded controller only supplies board temperatures (no fan control) and each read waits on the EC
        // (~45 ms per poll on the test board), so it follows the slow cadence like DIMMs.
        return IsDimm(hardware) || hardware.HardwareType == HardwareType.EmbeddedController ? IsDue(hardware) : true;
    }

    /// <summary>Memory nodes other than the generic "/ram" and "/vram" ones are DIMMs read over SMBus.</summary>
    private static bool IsDimm(IHardware hardware)
    {
        if (hardware.HardwareType != HardwareType.Memory) return false;
        var id = hardware.Identifier.ToString();
        return id != "/ram" && id != "/vram";
    }

    /// <summary>True when the node was never updated or its last update is older than <see cref="SlowInterval"/>.</summary>
    private bool IsDue(IHardware hardware)
    {
        var id = hardware.Identifier.ToString();
        var now = Stopwatch.GetTimestamp();
        if (_slowUpdatedAt.TryGetValue(id, out var last) && Stopwatch.GetElapsedTime(last, now) < SlowInterval) return false;
        _slowUpdatedAt[id] = now;
        return true;
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }

    private sealed class NodeTiming
    {
        public int Count;
        public long Total;
        public long Max;
        public TimeSpan CpuTotal;
    }

    /// <summary>CPU time (user + kernel) of the calling thread; zero where unsupported.</summary>
    private static partial class ThreadCpu
    {
        public static TimeSpan Now()
        {
            if (!OperatingSystem.IsWindows()) return TimeSpan.Zero;
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
                ? TimeSpan.FromTicks(kernel + user)
                : TimeSpan.Zero;
        }

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static partial bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    }
}
