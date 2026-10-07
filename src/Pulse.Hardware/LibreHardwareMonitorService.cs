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

    private readonly ILogger<LibreHardwareMonitorService> _logger;

    /// <summary>Serialises every LibreHardwareMonitor call (Open/Update/Set*/Close).</summary>
    private readonly object _lhmLock = new();
    private readonly object _initLock = new();

    /// <summary>Fan id → software duty currently applied by us.</summary>
    private readonly ConcurrentDictionary<string, double> _manualFans = new(StringComparer.Ordinal);
    /// <summary>Controls we wrote to; restored to firmware defaults on dispose. Guarded by <see cref="_lhmLock"/>.</summary>
    private readonly Dictionary<string, IControl> _touchedControls = new(StringComparer.Ordinal);

    // LHM types are only touched after InitializeAsync so that a missing native/managed library
    // (the package ships Windows runtimes only) surfaces as Unsupported instead of a constructor failure.
    private Computer? _computer;                                     // guarded by _lhmLock
    private UpdateVisitor? _updateVisitor;                           // guarded by _lhmLock
    private Dictionary<string, FanBinding> _fanBindings = new();     // guarded by _lhmLock
    private HardwareSnapshot? _lastSnapshot;                         // guarded by _lhmLock
    private Task? _initTask;                                         // guarded by _initLock
    private volatile HardwareMonitorStatus _status;
    private int _disposed;

    public LibreHardwareMonitorService(ILogger<LibreHardwareMonitorService>? logger = null)
    {
        _logger = logger ?? NullLogger<LibreHardwareMonitorService>.Instance;
        _status = new HardwareMonitorStatus { IsElevated = DetectElevation() };
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
                    var result = SnapshotBuilder.Build(computer.Hardware, _manualFans);
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
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        lock (_lhmLock)
        {
            foreach (var (fanId, control) in _touchedControls)
            {
                try
                {
                    control.SetDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to restore default control for fan {FanId}", fanId);
                }
            }

            _touchedControls.Clear();
            _manualFans.Clear();
            _fanBindings = new Dictionary<string, FanBinding>();
            CloseComputerLocked();
        }
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
                result = SnapshotBuilder.Build(computer.Hardware, _manualFans);
                _fanBindings = result.FanBindings;
                _lastSnapshot = result.Snapshot;
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

        SetStatus(_status with
        {
            State = healthy ? HardwareMonitorState.Ready : HardwareMonitorState.Degraded,
            CpuTemperatureAvailable = result.CpuTemperatureAvailable,
            FanControlAvailable = result.FanControlAvailable,
            Message = message,
        });
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
