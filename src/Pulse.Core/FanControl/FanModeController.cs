using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Core.FanControl;

/// <summary>Immutable copy of the fan-mode settings, taken on the UI thread and handed to the controller.</summary>
public sealed record FanControlSettings
{
    public FanMode Mode { get; init; } = FanMode.Individual;
    public double SyncedPercent { get; init; } = 50;
    public IReadOnlySet<string> ExcludedIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> IncludedIds { get; init; } = new HashSet<string>();

    public static FanControlSettings From(AppSettings s) => new()
    {
        Mode = s.FanMode,
        SyncedPercent = s.SyncedFanPercent,
        ExcludedIds = new HashSet<string>(s.FanGroupExcludedIds ?? new List<string>(), StringComparer.Ordinal),
        IncludedIds = new HashSet<string>(s.FanGroupIncludedIds ?? new List<string>(), StringComparer.Ordinal),
    };
}

/// <summary>Per-fan outcome of the last evaluation, for the UI chips.</summary>
public enum FanModeState
{
    /// <summary>個別 mode: the per-fan card is in charge.</summary>
    Individual,
    /// <summary>自動 mode: firmware curve.</summary>
    Auto,
    /// <summary>Quiet curve applied (<see cref="FanModeFanStatus.Percent"/>).</summary>
    Quiet,
    /// <summary>Synced duty applied (<see cref="FanModeFanStatus.Percent"/>).</summary>
    Synced,
    /// <summary>Quiet: too hot (≥ 75 °C, until ≤ 68 °C) → firmware.</summary>
    HandedOff,
    /// <summary>Quiet: no source temperature → firmware.</summary>
    NoTemperature,
    /// <summary>Not a group member (<see cref="FanModeFanStatus.Exclusion"/>).</summary>
    Excluded,
    /// <summary>Header without a detected fan (hidden) or still in the detection grace period: never written.</summary>
    NotDetected,
    /// <summary>Monitor-only fan.</summary>
    NotControllable,
    /// <summary>Fan control unavailable in this process (e.g. not elevated).</summary>
    Unavailable,
}

public sealed record FanModeFanStatus(
    string FanId,
    FanModeState State,
    double? Percent,
    FanExclusion Exclusion,
    bool IsGpuFan,
    double? SourceTempC,
    FanPresence Presence,
    string? Error = null);

/// <summary>Summary of one evaluation (published to the UI).</summary>
public sealed record FanModeResult
{
    public FanMode Mode { get; init; }
    public bool ControlAvailable { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    public IReadOnlyDictionary<string, FanModeFanStatus> Fans { get; init; } = new Dictionary<string, FanModeFanStatus>();

    public static FanModeResult Empty { get; } = new();
}

/// <summary>
/// The fan-mode state machine. Each <see cref="UpdateAsync"/> evaluates one hardware snapshot against the settings and
/// applies the needed writes through <see cref="IHardwareMonitor.SetFanAsync"/>, sequentially: hand-backs first, then
/// duty writes. Calls are serialised internally; after <see cref="Stop"/> nothing is ever written again.
/// </summary>
public sealed class FanModeController
{
    /// <summary>Synced: re-apply when the reported duty drifts this far from the target (another tool changed it).</summary>
    public const double SyncedDriftPercent = 5;

    private readonly IHardwareMonitor _hardware;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Fans this controller put in manual mode → duty last applied.</summary>
    private readonly Dictionary<string, double> _controlled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastFailure = new(StringComparer.Ordinal);
    /// <summary>Quiet: fans currently handed back because of heat (or a missing temperature), until ≤ 68 °C.</summary>
    private readonly HashSet<string> _handedOff = new(StringComparer.Ordinal);
    private FanMode? _lastMode;
    private volatile bool _stopped;

    public FanModeController(IHardwareMonitor hardware, ILogger? logger = null, Func<DateTimeOffset>? clock = null)
    {
        _hardware = hardware;
        _log = logger ?? NullLogger.Instance;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public bool IsStopped => _stopped;

    /// <summary>Fans currently held in manual mode by this controller (id → duty). For diagnostics / tests.</summary>
    public IReadOnlyDictionary<string, double> Controlled
    {
        get { lock (_controlled) return new Dictionary<string, double>(_controlled); }
    }

    /// <summary>Stops all further writes. Fans are restored by <see cref="IHardwareMonitor.Dispose"/>, not here.</summary>
    public void Stop() => _stopped = true;

    /// <summary>Waits (bounded) for an in-flight <see cref="UpdateAsync"/> to finish. Call after <see cref="Stop"/>.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        if (!_gate.Wait(timeout)) return false;
        _gate.Release();
        return true;
    }

    public async Task<FanModeResult> UpdateAsync(
        HardwareSnapshot snapshot,
        FanControlSettings settings,
        bool controlAvailable,
        FanPresenceSnapshot? presence = null,
        CancellationToken cancellationToken = default)
    {
        presence ??= FanPresenceSnapshot.AllVisible;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var gpuTemp = snapshot.Gpus.Select(g => g.CoreTempC).FirstOrDefault(t => t is not null);
            var result = new FanModeResult
            {
                Mode = settings.Mode,
                ControlAvailable = controlAvailable,
                CpuTempC = snapshot.Cpu?.PackageTempC,
                GpuTempC = gpuTemp,
            };
            if (_stopped) return result;

            var now = _clock();
            var statuses = new Dictionary<string, FanModeFanStatus>(StringComparer.Ordinal);

            if (!controlAvailable)
            {
                // Nothing can be written; forget the mode so the next available snapshot starts cleanly.
                _lastMode = null;
                foreach (var fan in snapshot.Fans)
                {
                    statuses[fan.Id] = new FanModeFanStatus(fan.Id, FanModeState.Unavailable, null, FanExclusion.None,
                        IsGpuFan(fan, snapshot), null, presence.PresenceOf(fan.Id));
                }
                return result with { Fans = statuses };
            }

            // Ordered plan: every hand-back runs before any duty write.
            var handBacks = new List<string>();
            var writes = new List<(string Id, double Percent)>();
            void HandBack(string id)
            {
                if (!handBacks.Contains(id)) handBacks.Add(id);
            }
            bool HeldByUs(string id) => _controlled.ContainsKey(id) && !handBacks.Contains(id);

            if (_lastMode != settings.Mode)
            {
                // Mode switch: hand back everything we hold and every fan left manual by the per-fan cards.
                // (At startup in 個別 mode there is nothing to hand back: the cards own their fans.)
                if (_lastMode is not null || settings.Mode != FanMode.Individual)
                {
                    foreach (var id in _controlled.Keys) HandBack(id);
                    foreach (var fan in snapshot.Fans)
                    {
                        if (fan.CanControl && fan.IsManual) HandBack(fan.Id);
                    }
                }
                _log.LogInformation("Fan mode {From} → {To} (handing back {Count} fan(s))",
                    _lastMode?.ToString() ?? "(start)", settings.Mode, handBacks.Count);
                _handedOff.Clear();
                _lastMode = settings.Mode;
            }

            foreach (var fan in snapshot.Fans)
            {
                var isGpu = IsGpuFan(fan, snapshot);
                var fanPresence = presence.PresenceOf(fan.Id);
                FanModeFanStatus Status(FanModeState state, double? percent = null, FanExclusion exclusion = FanExclusion.None, double? temp = null)
                    => new(fan.Id, state, percent, exclusion, isGpu, temp, fanPresence);

                if (!fan.CanControl)
                {
                    lock (_controlled) _controlled.Remove(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.NotControllable);
                    continue;
                }

                if (settings.Mode == FanMode.Individual)
                {
                    statuses[fan.Id] = Status(FanModeState.Individual);
                    continue;
                }

                if (settings.Mode == FanMode.Auto)
                {
                    if (HeldByUs(fan.Id)) HandBack(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.Auto);
                    continue;
                }

                // Quiet / Synced from here on.
                if (fanPresence != FanPresence.Visible)
                {
                    if (HeldByUs(fan.Id)) HandBack(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.NotDetected);
                    continue;
                }

                var exclusion = FanGroupPolicy.Evaluate(fan, presence.FirstSight(fan.Id), settings.ExcludedIds, settings.IncludedIds);
                if (exclusion != FanExclusion.None)
                {
                    if (HeldByUs(fan.Id)) HandBack(fan.Id);
                    _handedOff.Remove(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.Excluded, exclusion: exclusion);
                    continue;
                }

                if (settings.Mode == FanMode.Synced)
                {
                    var target = QuietFanCurve.ClampToFan(fan, double.IsFinite(settings.SyncedPercent) ? settings.SyncedPercent : 50);
                    var held = HeldByUs(fan.Id);
                    var write = !held
                        || Math.Abs(_controlled[fan.Id] - target) >= 0.5
                        || (fan.Percent is { } reported && Math.Abs(reported - target) >= SyncedDriftPercent && Elapsed(fan.Id, now));
                    if (write && !RecentlyFailed(fan.Id, now)) writes.Add((fan.Id, target));
                    statuses[fan.Id] = Status(FanModeState.Synced, target);
                    continue;
                }

                // Quiet.
                var temp = isGpu ? GpuTempFor(fan, snapshot) : snapshot.Cpu?.PackageTempC;
                if (temp is not { } t || !double.IsFinite(t))
                {
                    _handedOff.Add(fan.Id);
                    if (HeldByUs(fan.Id)) HandBack(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.NoTemperature);
                    continue;
                }

                if (t >= QuietFanCurve.HandoffAtC || (_handedOff.Contains(fan.Id) && t > QuietFanCurve.ResumeAtC))
                {
                    if (_handedOff.Add(fan.Id))
                    {
                        _log.LogInformation("Quiet: fan {Fan} handed back to firmware at {Temp:0.#}°C", fan.Name, t);
                    }
                    if (HeldByUs(fan.Id)) HandBack(fan.Id);
                    statuses[fan.Id] = Status(FanModeState.HandedOff, temp: t);
                    continue;
                }

                if (_handedOff.Remove(fan.Id))
                {
                    _log.LogInformation("Quiet: fan {Fan} resumes the curve at {Temp:0.#}°C", fan.Name, t);
                }

                var quiet = QuietFanCurve.TargetFor(fan, isGpu, t);
                double shown;
                if (!HeldByUs(fan.Id))
                {
                    if (!RecentlyFailed(fan.Id, now)) writes.Add((fan.Id, quiet));
                    shown = quiet;
                }
                else
                {
                    var last = _controlled[fan.Id];
                    if (Math.Abs(quiet - last) >= QuietFanCurve.MinStepPercent && Elapsed(fan.Id, now))
                    {
                        writes.Add((fan.Id, quiet));
                        shown = quiet;
                    }
                    else
                    {
                        shown = last;
                    }
                }
                statuses[fan.Id] = Status(FanModeState.Quiet, shown, temp: t);
            }

            // Execute: hand-backs first, then writes. Stop() aborts between calls.
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in handBacks)
            {
                if (_stopped) break;
                try
                {
                    await _hardware.SetFanAsync(id, null, cancellationToken).ConfigureAwait(false);
                    lock (_controlled) _controlled.Remove(id);
                    _lastWrite[id] = now;
                    _lastFailure.Remove(id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep it in _controlled so the next evaluation retries (throttled).
                    _lastFailure[id] = now;
                    errors[id] = ex.Message;
                    _log.LogWarning(ex, "Fan mode: handing fan {FanId} back failed", id);
                }
            }

            foreach (var (id, percent) in writes)
            {
                if (_stopped) break;
                if (errors.ContainsKey(id)) continue;
                try
                {
                    await _hardware.SetFanAsync(id, percent, cancellationToken).ConfigureAwait(false);
                    lock (_controlled) _controlled[id] = percent;
                    _lastWrite[id] = now;
                    _lastFailure.Remove(id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastFailure[id] = now;
                    errors[id] = ex.Message;
                    _log.LogWarning(ex, "Fan mode: setting fan {FanId} to {Percent:0}% failed", id, percent);
                }
            }

            foreach (var (id, message) in errors)
            {
                if (statuses.TryGetValue(id, out var s)) statuses[id] = s with { Error = message };
            }

            return result with { Fans = statuses };
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Elapsed(string id, DateTimeOffset now)
        => !_lastWrite.TryGetValue(id, out var at) || now - at >= QuietFanCurve.MinWriteInterval;

    private bool RecentlyFailed(string id, DateTimeOffset now)
        => _lastFailure.TryGetValue(id, out var at) && now - at < QuietFanCurve.MinWriteInterval;

    /// <summary>GPU fan: belongs to a GPU node (id prefix) or sits in the GPU group.</summary>
    public static bool IsGpuFan(FanInfo fan, HardwareSnapshot snapshot)
        => FanGroupPolicy.IsGpuGroup(fan.Group) || OwningGpu(fan, snapshot) is not null;

    private static GpuInfo? OwningGpu(FanInfo fan, HardwareSnapshot snapshot)
    {
        foreach (var gpu in snapshot.Gpus)
        {
            if (fan.Id.StartsWith(gpu.Id + "/", StringComparison.Ordinal)) return gpu;
        }
        return null;
    }

    /// <summary>Core temperature of the GPU the fan belongs to; the first GPU when the id does not tell.</summary>
    public static double? GpuTempFor(FanInfo fan, HardwareSnapshot snapshot)
        => (OwningGpu(fan, snapshot) ?? snapshot.Gpus.FirstOrDefault())?.CoreTempC;
}
