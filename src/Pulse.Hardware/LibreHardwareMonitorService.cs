using System.Collections.Concurrent;
using System.Security.Principal;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Hardware;

/// <summary>
/// <see cref="IHardwareMonitor"/> backed by LibreHardwareMonitorLib. Every call into the library is serialised
/// through one lock and runs on the thread pool; the status record is swapped atomically.
/// </summary>
public sealed class LibreHardwareMonitorService : IHardwareMonitor
{
    private const string NotElevatedMessage = "未以系統管理員身分執行，無法讀取 CPU 溫度與控制風扇。";
    private const string DriverMissingMessage = "核心驅動程式未載入：請安裝 PawnIO（https://pawnio.eu），或檢查記憶體完整性設定。";
    private const string NoControllableFanMessage = "未偵測到可控制的風扇（主機板感測晶片可能不受支援）。";
    private const string UnsupportedPlatformMessage = "此平台尚未支援硬體監控。";
    private const string MissingComponentMessage = "此平台缺少硬體監控所需的元件（LibreHardwareMonitorLib 僅提供 Windows 版本）。";
    private const string NotReadyMessage = "硬體監控尚未就緒，無法控制風扇。";

    private const string RecoveredGpuNotice = "Pulse 上次未正常結束，已將停在手動轉速的顯示卡風扇交回自動。";
    private const string StuckBoardFansNoticeFormat =
        "Pulse 上次未正常結束，主機板風扇（{0}）可能仍固定在手動轉速。主機板晶片無法在結束後還原 BIOS 曲線，請重新開機以恢復自動控制，或在下方自行設定轉速。";

    /// <summary>How long Dispose waits for an in-flight LHM call before restoring fans anyway.</summary>
    private static readonly TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<LibreHardwareMonitorService> _logger;
    private readonly FanRecoveryJournal _journal;

    /// <summary>Serialises every LibreHardwareMonitor call (Open/Update/Set*/Close).</summary>
    private readonly object _lhmLock = new();
    private readonly object _initLock = new();

    /// <summary>Fan id → software duty currently applied by us.</summary>
    private readonly ConcurrentDictionary<string, double> _manualFans = new(StringComparer.Ordinal);
    /// <summary>Controls we wrote to; restored to firmware defaults on dispose. Guarded by <see cref="_lhmLock"/>.</summary>
    private readonly Dictionary<string, IControl> _touchedControls = new(StringComparer.Ordinal);
    /// <summary>Journal entries from a previous session this process cannot see yet (non-elevated). Guarded by <see cref="_lhmLock"/>.</summary>
    private readonly List<FanRecoveryJournal.Entry> _carriedOver = new();

    // LHM types are only touched after InitializeAsync so that a missing native/managed library
    // (the package ships Windows runtimes only) surfaces as Unsupported instead of a constructor failure.
    private Computer? _computer;                                     // guarded by _lhmLock
    private UpdateVisitor? _updateVisitor;                           // guarded by _lhmLock
    private Dictionary<string, FanBinding> _fanBindings = new();     // guarded by _lhmLock
    private HardwareSnapshot? _lastSnapshot;                         // guarded by _lhmLock
    private Task? _initTask;                                         // guarded by _initLock
    private volatile HardwareMonitorStatus _status;
    private int _disposed;

    /// <summary>
    /// Whether fan writes can take effect in this process. On Windows without elevation LHM still reports GPU fans as
    /// controllable and SetSoftware "succeeds", but the driver ignores it (measured: duty and RPM unchanged), so every
    /// fan is reported monitor-only there.
    /// </summary>
    private readonly bool _controlAllowed;

    /// <param name="recoveryJournalPath">Where manual fans are journaled; defaults to %LOCALAPPDATA%\Pulse\fan-recovery.json.</param>
    public LibreHardwareMonitorService(ILogger<LibreHardwareMonitorService>? logger = null, string? recoveryJournalPath = null)
    {
        _logger = logger ?? NullLogger<LibreHardwareMonitorService>.Instance;
        _journal = new FanRecoveryJournal(recoveryJournalPath ?? FanRecoveryJournal.DefaultPath, _logger);
        _status = new HardwareMonitorStatus { IsElevated = DetectElevation() };
        _controlAllowed = !OperatingSystem.IsWindows() || _status.IsElevated;
    }

    public HardwareMonitorStatus Status => _status;

    public event EventHandler<HardwareMonitorStatus>? StatusChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        lock (_initLock)
        {
            if (_initTask is not null && _status.State is not (HardwareMonitorState.NotStarted or HardwareMonitorState.Failed))
            {
                return _initTask;
            }

            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                _logger.LogInformation("Hardware monitoring is not supported on this platform");
                SetStatus(_status with { State = HardwareMonitorState.Unsupported, Message = UnsupportedPlatformMessage });
                return _initTask = Task.CompletedTask;
            }

            SetStatus(_status with { State = HardwareMonitorState.Initializing, Message = null });
            // The token is handled inside so the status never gets stuck at Initializing.
            return _initTask = Task.Run(() => RunInitialize(cancellationToken), CancellationToken.None);
        }
    }

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return Task.FromResult(HardwareSnapshot.Empty);

        return Task.Run(() =>
        {
            lock (_lhmLock)
            {
                if (_computer is not { } computer || _updateVisitor is not { } visitor) return HardwareSnapshot.Empty;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    computer.Accept(visitor);
                    var result = SnapshotBuilder.Build(computer.Hardware, _manualFans, _controlAllowed);
                    _fanBindings = result.FanBindings;
                    _lastSnapshot = result.Snapshot;
                    return result.Snapshot;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hardware snapshot failed; returning the previous snapshot");
                    return _lastSnapshot ?? HardwareSnapshot.Empty;
                }
            }
        }, cancellationToken);
    }

    public Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fanId);
        if (percent is { } p && double.IsNaN(p))
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "風扇轉速百分比必須是數值。");
        }

        ThrowIfDisposed();
        if (!IsRunning)
        {
            _logger.LogWarning("SetFan({FanId}) rejected: monitor state is {State}", fanId, _status.State);
            throw new InvalidOperationException(NotReadyMessage);
        }

        return Task.Run(() =>
        {
            lock (_lhmLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Dispose may have restored the fans while this call waited for the lock: never write after that.
                ThrowIfDisposed();

                if (!_fanBindings.TryGetValue(fanId, out var binding) || binding.Control is not { } control)
                {
                    _logger.LogWarning("SetFan({FanId}) rejected: fan unknown or has no control channel", fanId);
                    throw new InvalidOperationException($"找不到風扇「{fanId}」，或此風扇不支援轉速控制。");
                }

                if (percent is null)
                {
                    control.SetDefault();
                    _manualFans.TryRemove(fanId, out _);
                    _touchedControls.Remove(fanId);
                    _logger.LogInformation("Fan {FanId} handed back to firmware control", fanId);
                }
                else
                {
                    var value = Math.Clamp(percent.Value, control.MinSoftwareValue, control.MaxSoftwareValue);
                    control.SetSoftware((float)value);
                    _manualFans[fanId] = value;
                    _touchedControls[fanId] = control;
                    _logger.LogInformation("Fan {FanId} set to {Percent}% (requested {Requested}%)", fanId, value, percent.Value);
                }

                WriteJournalLocked();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Hands every fan we touched back to the firmware and closes LHM. Safe to call from crash / process-exit handlers:
    /// if another thread is stuck inside LHM it waits at most <see cref="DisposeLockTimeout"/> and restores anyway.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var locked = Monitor.TryEnter(_lhmLock, DisposeLockTimeout);
        if (!locked) _logger.LogWarning("Hardware monitor busy during dispose; restoring fans without the lock");
        try
        {
            var allRestored = true;
            foreach (var (fanId, control) in _touchedControls.ToList())
            {
                try
                {
                    control.SetDefault();
                    _logger.LogInformation("Fan {FanId} handed back to firmware control on shutdown", fanId);
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    _logger.LogWarning(ex, "Failed to restore default control for fan {FanId}", fanId);
                }
            }

            // On success the journal shrinks to entries still awaiting an elevated start (usually: deleted).
            // When a restore failed it is left as is, so the next start can still act on it.
            _touchedControls.Clear();
            _manualFans.Clear();
            if (allRestored) WriteJournalLocked();
            _fanBindings = new Dictionary<string, FanBinding>();
            if (locked) CloseComputerLocked();
        }
        finally
        {
            if (locked) Monitor.Exit(_lhmLock);
        }
    }

    /// <summary>Caller holds <see cref="_lhmLock"/>.</summary>
    private void WriteJournalLocked()
    {
        var entries = _manualFans
            .Select(kv => new FanRecoveryJournal.Entry(
                kv.Key,
                _fanBindings.TryGetValue(kv.Key, out var binding) ? binding.Fan.Name : kv.Key,
                kv.Value))
            .Concat(_carriedOver.Where(c => !_manualFans.ContainsKey(c.Id)))
            .ToList();
        _journal.Write(entries);
    }

    /// <summary>
    /// Fans a previous session left in manual mode. GPU drivers accept an explicit "auto" request at any time, so those
    /// are restored; Super I/O / EC chips only restore the mode saved by the process that changed it, which is gone,
    /// so they can only be reported. Caller holds <see cref="_lhmLock"/>. Returns the notice for the UI, or null.
    /// </summary>
    private string? RecoverFromPreviousSessionLocked()
    {
        var leftovers = _journal.Load();
        if (leftovers.Count == 0) return null;

        var restoredGpu = 0;
        var stuck = new List<string>();
        _carriedOver.Clear();
        foreach (var entry in leftovers)
        {
            if (!_fanBindings.TryGetValue(entry.Id, out var binding) || binding.Control is not { } control)
            {
                if (!_status.IsElevated)
                {
                    // Motherboard fans are invisible without elevation; keep the entry for an elevated start.
                    _carriedOver.Add(entry);
                    continue;
                }
                _logger.LogWarning("Fan {FanId} ({Name}) was left at {Percent}% by the previous session but is no longer present", entry.Id, entry.Name, entry.Percent);
                continue;
            }

            if (binding.Fan.Hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                try
                {
                    control.SetDefault();
                    restoredGpu++;
                    _logger.LogWarning("Fan {FanId} ({Name}) was left at {Percent}% by the previous session; handed back to the driver", entry.Id, entry.Name, entry.Percent);
                }
                catch (Exception ex)
                {
                    stuck.Add(entry.Name);
                    _logger.LogWarning(ex, "Could not restore fan {FanId} left in manual mode by the previous session", entry.Id);
                }
            }
            else
            {
                stuck.Add(entry.Name);
                _logger.LogWarning("Fan {FanId} ({Name}) may still be fixed at {Percent}% from the previous session; Super I/O cannot be restored without a reboot", entry.Id, entry.Name, entry.Percent);
            }
        }

        WriteJournalLocked();
        if (stuck.Count > 0) return string.Format(StuckBoardFansNoticeFormat, string.Join("、", stuck));
        return restoredGpu > 0 ? RecoveredGpuNotice : null;
    }

    private bool IsRunning => _disposed == 0 && _status.State is HardwareMonitorState.Ready or HardwareMonitorState.Degraded;

    /// <summary>Keeps LHM types out of this frame so a missing library fails inside the catch, not while JIT-compiling the callee.</summary>
    private void RunInitialize(CancellationToken cancellationToken)
    {
        try
        {
            InitializeCore(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("LibreHardwareMonitor initialization cancelled");
            lock (_lhmLock) CloseComputerLocked();
            SetStatus(_status with { State = HardwareMonitorState.NotStarted, Message = null });
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or TypeLoadException
                                        or BadImageFormatException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "LibreHardwareMonitor is unavailable on this platform");
            lock (_lhmLock) CloseComputerLocked();
            SetStatus(_status with { State = HardwareMonitorState.Unsupported, Message = MissingComponentMessage });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LibreHardwareMonitor initialization failed");
            lock (_lhmLock) CloseComputerLocked();
            SetStatus(_status with { State = HardwareMonitorState.Failed, Message = $"硬體監控初始化失敗：{ex.Message}" });
        }
    }

    private void InitializeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Opening LibreHardwareMonitor (elevated: {Elevated})", _status.IsElevated);

        SnapshotBuildResult? result = null;
        string? recoveryNotice = null;
        lock (_lhmLock)
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = true,
                IsPsuEnabled = true,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsBatteryEnabled = false,
            };
            _computer = computer;
            _updateVisitor ??= new UpdateVisitor(_logger);
            computer.Open();

            if (_disposed != 0 || cancellationToken.IsCancellationRequested)
            {
                CloseComputerLocked();
            }
            else
            {
                // First refresh decides what this machine exposes (CPU temperatures need the kernel driver).
                computer.Accept(_updateVisitor);
                result = SnapshotBuilder.Build(computer.Hardware, _manualFans, _controlAllowed);
                _fanBindings = result.FanBindings;
                _lastSnapshot = result.Snapshot;

                recoveryNotice = RecoverFromPreviousSessionLocked();
            }
        }

        if (result is null)
        {
            // Disposed (or cancelled) while opening: nothing to report.
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus(_status with { State = HardwareMonitorState.NotStarted, Message = null });
            return;
        }

        var healthy = result.CpuTemperatureAvailable && result.FanControlAvailable;
        var message = healthy ? null
            : !_status.IsElevated ? NotElevatedMessage
            : !result.CpuTemperatureAvailable ? DriverMissingMessage
            : NoControllableFanMessage;

        _logger.LogInformation(
            "LibreHardwareMonitor ready: cpuTemp={CpuTemp} fanControl={FanControl} fans={Fans} gpus={Gpus} otherTemps={OtherTemps}",
            result.CpuTemperatureAvailable, result.FanControlAvailable,
            result.Snapshot.Fans.Count, result.Snapshot.Gpus.Count, result.Snapshot.OtherTemperatures.Count);
        LogFans(result);

        SetStatus(_status with
        {
            State = healthy ? HardwareMonitorState.Ready : HardwareMonitorState.Degraded,
            CpuTemperatureAvailable = result.CpuTemperatureAvailable,
            FanControlAvailable = result.FanControlAvailable,
            Message = message,
            RecoveryNotice = recoveryNotice,
        });
    }

    /// <summary>One Information line describing every fan (helps map generic Super I/O names like "Fan #1" to headers).</summary>
    private void LogFans(SnapshotBuildResult result)
    {
        var fans = result.Snapshot.Fans;
        if (fans.Count == 0)
        {
            _logger.LogInformation("Fans: none reported (control allowed: {Allowed})", _controlAllowed);
            return;
        }

        var list = string.Join(" | ", fans.Select(f =>
        {
            var channel = result.FanBindings.TryGetValue(f.Id, out var b) && b.ControlSensor is not null;
            return $"{f.Id} \"{f.Name}\" [{f.Group}] rpm={Fmt(f.Rpm)} duty={Fmt(f.Percent)}% min/max={f.MinPercent:0}/{f.MaxPercent:0} channel={channel} canControl={f.CanControl}";
        }));
        _logger.LogInformation("Fans ({Count}, control allowed: {Allowed}): {Fans}", fans.Count, _controlAllowed, list);

        static string Fmt(double? v) => v is { } x ? Math.Round(x).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
    }

    /// <summary>Caller holds <see cref="_lhmLock"/>.</summary>
    private void CloseComputerLocked()
    {
        if (_computer is not { } computer) return;
        _computer = null;

        try
        {
            computer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Computer.Close failed");
        }
    }

    private void SetStatus(HardwareMonitorStatus status)
    {
        _status = status;

        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StatusChanged handler threw");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private static bool DetectElevation()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }

            return Environment.IsPrivilegedProcess;
        }
        catch
        {
            return false;
        }
    }
}
