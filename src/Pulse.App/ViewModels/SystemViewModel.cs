using System.Collections.ObjectModel;
using System.ComponentModel;
using Pulse.App.Controls;
using Pulse.App.Localization;
using Pulse.App.Services;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Pulse.App.ViewModels;

/// <summary>系統 tab: CPU / GPU / memory telemetry. Every value is an observable property refreshed from <c>Monitoring.HardwareUpdated</c>.</summary>
public sealed partial class SystemViewModel : TabViewModelBase
{
    public const int MaxCoreBars = 20;

    private readonly ILogger<SystemViewModel> _log;

    // ---- banners ----
    [ObservableProperty] private bool _showElevationBanner;
    [ObservableProperty] private string? _hardwareMessage;
    [ObservableProperty] private bool _hasHardwareMessage;
    [ObservableProperty] private bool _hasData;

    // ---- CPU ----
    [ObservableProperty] private bool _hasCpu;
    [ObservableProperty] private string _cpuName = Strings.Cpu;
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuLoadText = Strings.NotAvailable;
    [ObservableProperty] private string _cpuTempText = Strings.NotAvailable;
    [ObservableProperty] private BccTone _cpuTempTone = BccTone.Neutral;
    [ObservableProperty] private bool _cpuTempUnavailable;
    [ObservableProperty] private string _cpuClockText = Strings.NotAvailable;
    [ObservableProperty] private string _cpuPowerText = Strings.NotAvailable;
    [ObservableProperty] private string _coreCountText = string.Empty;
    [ObservableProperty] private bool _hasCores;

    // ---- Memory ----
    [ObservableProperty] private bool _hasMemory;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryText = Strings.NotAvailable;
    [ObservableProperty] private string _memoryPercentText = Strings.NotAvailable;

    // ---- Other temperatures ----
    [ObservableProperty] private bool _hasOtherTemperatures;
    [ObservableProperty] private string _otherTemperaturesHeader = Strings.OtherTemperatures;

    public SystemViewModel(MonitoringService monitoring, SettingsService settings, IElevationService elevation, ILogger<SystemViewModel> log)
        : base(MainTab.System, monitoring, settings)
    {
        Elevation = elevation;
        _log = log;
        ShowElevationBanner = !elevation.IsElevated;

        Monitoring.HardwareUpdated += (_, snapshot) => Apply(snapshot);
        Monitoring.PropertyChanged += OnMonitoringPropertyChanged;
        ApplyStatus(Monitoring.HardwareStatus);
        Apply(Monitoring.LatestHardware);
    }

    public IElevationService Elevation { get; }

    public bool IsElevated => Elevation.IsElevated;

    public ObservableCollection<CoreLoadItem> CoreLoads { get; } = new();

    public ObservableCollection<GpuItemViewModel> Gpus { get; } = new();

    public ObservableCollection<StorageItemViewModel> Storages { get; } = new();

    public ObservableCollection<TemperatureItem> OtherTemperatures { get; } = new();

    public override string PlaceholderText => Strings.PlaceholderSystem;

    private void OnMonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitoringService.HardwareStatus)) ApplyStatus(Monitoring.HardwareStatus);
    }

    private void ApplyStatus(HardwareMonitorStatus status)
    {
        HardwareMessage = status.Message;
        HasHardwareMessage = !string.IsNullOrWhiteSpace(status.Message);
        ShowElevationBanner = !Elevation.IsElevated && !status.IsElevated;
    }

    private void Apply(HardwareSnapshot snapshot)
    {
        HasData = snapshot.Cpu is not null || snapshot.Gpus.Count > 0 || snapshot.Memory is not null || snapshot.Storages.Count > 0;

        // CPU
        if (snapshot.Cpu is { } cpu)
        {
            HasCpu = true;
            CpuName = cpu.Name;
            CpuLoad = cpu.LoadPercent ?? 0;
            CpuLoadText = Strings.Percent(cpu.LoadPercent);
            CpuTempText = Strings.Celsius(cpu.PackageTempC);
            CpuTempTone = BccTones.ForTemperature(cpu.PackageTempC);
            CpuTempUnavailable = cpu.PackageTempC is null;
            CpuClockText = cpu.ClockMhz is { } mhz ? $"{mhz / 1000:0.0} GHz" : Strings.NotAvailable;
            CpuPowerText = cpu.PowerWatts is { } w ? $"{w:0} W" : Strings.NotAvailable;
            SyncCores(cpu.CoreLoadsPercent);
        }
        else
        {
            HasCpu = false;
            SyncCores(Array.Empty<double?>());
        }

        // GPUs (keyed by id, in place)
        SyncGpus(snapshot.Gpus);

        // Memory
        if (snapshot.Memory is { } mem && (mem.UsedGb is not null || mem.TotalGb is not null))
        {
            HasMemory = true;
            var pct = mem.LoadPercent ?? (mem.UsedGb is { } u && mem.TotalGb is { } t && t > 0 ? u / t * 100 : 0);
            MemoryPercent = Math.Clamp(pct, 0, 100);
            MemoryText = Strings.Gigabytes(mem.UsedGb, mem.TotalGb);
            MemoryPercentText = Strings.Percent(pct);
        }
        else
        {
            HasMemory = false;
        }

        // Drives (keyed by id, in place)
        SyncStorages(snapshot.Storages);

        // Other temperatures
        SyncTemperatures(snapshot.OtherTemperatures);
    }

    private void SyncStorages(IReadOnlyList<StorageInfo> drives)
    {
        var sameOrder = drives.Count == Storages.Count;
        for (var i = 0; sameOrder && i < drives.Count; i++)
        {
            if (Storages[i].Id != drives[i].Id) sameOrder = false;
        }

        if (sameOrder)
        {
            for (var i = 0; i < drives.Count; i++) Storages[i].Update(drives[i]);
            return;
        }

        var existing = Storages.ToDictionary(s => s.Id);
        Storages.Clear();
        foreach (var drive in drives)
        {
            if (!existing.TryGetValue(drive.Id, out var item)) item = new StorageItemViewModel(drive.Id);
            item.Update(drive);
            Storages.Add(item);
        }
    }

    private void SyncCores(IReadOnlyList<double?> loads)
    {
        var count = Math.Min(loads.Count, MaxCoreBars);
        while (CoreLoads.Count > count) CoreLoads.RemoveAt(CoreLoads.Count - 1);
        while (CoreLoads.Count < count) CoreLoads.Add(new CoreLoadItem(CoreLoads.Count));
        for (var i = 0; i < count; i++) CoreLoads[i].Update(loads[i]);
        HasCores = count > 0;
        CoreCountText = count > 0 ? string.Format(Strings.CoresFormat, loads.Count) : string.Empty;
    }

    private void SyncGpus(IReadOnlyList<GpuInfo> gpus)
    {
        var sameOrder = gpus.Count == Gpus.Count;
        if (sameOrder)
        {
            for (var i = 0; i < gpus.Count; i++)
            {
                if (Gpus[i].Id != gpus[i].Id) { sameOrder = false; break; }
            }
        }

        if (sameOrder)
        {
            for (var i = 0; i < gpus.Count; i++) Gpus[i].Update(gpus[i]);
            return;
        }

        var existing = Gpus.ToDictionary(g => g.Id);
        Gpus.Clear();
        foreach (var gpu in gpus)
        {
            if (!existing.TryGetValue(gpu.Id, out var item)) item = new GpuItemViewModel(gpu.Id);
            item.Update(gpu);
            Gpus.Add(item);
        }
        _log.LogDebug("GPU list: {Names}", string.Join(", ", gpus.Select(g => g.Name)));
    }

    private void SyncTemperatures(IReadOnlyList<TemperatureReading> temps)
    {
        var sameOrder = temps.Count == OtherTemperatures.Count;
        if (sameOrder)
        {
            for (var i = 0; i < temps.Count; i++)
            {
                if (OtherTemperatures[i].Id != temps[i].Id) { sameOrder = false; break; }
            }
        }

        if (sameOrder)
        {
            for (var i = 0; i < temps.Count; i++) OtherTemperatures[i].Update(temps[i]);
        }
        else
        {
            OtherTemperatures.Clear();
            foreach (var t in temps) OtherTemperatures.Add(new TemperatureItem(t));
        }

        HasOtherTemperatures = temps.Count > 0;
        OtherTemperaturesHeader = string.Format(Strings.OtherTemperaturesFormat, temps.Count);
    }
}

/// <summary>One tiny bar of the per-core load grid.</summary>
public sealed partial class CoreLoadItem : ObservableObject
{
    [ObservableProperty] private double _load;
    [ObservableProperty] private BccTone _tone = BccTone.Accent;
    [ObservableProperty] private string _tooltip = string.Empty;

    public CoreLoadItem(int index) => Index = index;

    public int Index { get; }

    public void Update(double? load)
    {
        Load = Math.Clamp(load ?? 0, 0, 100);
        Tone = BccTones.ForLoad(load);
        Tooltip = $"#{Index} {Strings.Percent(load)}";
    }
}

/// <summary>One GPU card.</summary>
public sealed partial class GpuItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name = Strings.Gpu;
    [ObservableProperty] private string _vendorText = string.Empty;
    [ObservableProperty] private double _load;
    [ObservableProperty] private string _loadText = Strings.NotAvailable;
    [ObservableProperty] private string _tempText = Strings.NotAvailable;
    [ObservableProperty] private BccTone _tempTone = BccTone.Neutral;
    [ObservableProperty] private string? _hotSpotText;
    [ObservableProperty] private BccTone _hotSpotTone = BccTone.Neutral;
    [ObservableProperty] private bool _hasHotSpot;
    [ObservableProperty] private bool _hasVram;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private string _vramText = Strings.NotAvailable;
    [ObservableProperty] private string? _fanText;
    [ObservableProperty] private bool _hasFan;
    [ObservableProperty] private string? _powerText;
    [ObservableProperty] private bool _hasPower;
    [ObservableProperty] private string? _clockText;
    [ObservableProperty] private bool _hasClock;

    public GpuItemViewModel(string id) => Id = id;

    public string Id { get; }

    public void Update(GpuInfo gpu)
    {
        Name = gpu.Name;
        VendorText = string.IsNullOrWhiteSpace(gpu.Vendor) || gpu.Vendor == "Unknown" ? Strings.Gpu : $"{Strings.Gpu} · {gpu.Vendor}";
        Load = gpu.LoadPercent ?? 0;
        LoadText = Strings.Percent(gpu.LoadPercent);
        TempText = Strings.Celsius(gpu.CoreTempC);
        TempTone = BccTones.ForTemperature(gpu.CoreTempC);
        HasHotSpot = gpu.HotSpotTempC is not null;
        HotSpotText = HasHotSpot ? $"{Strings.HotSpot} {Strings.Celsius(gpu.HotSpotTempC)}" : null;
        HotSpotTone = BccTones.ForTemperature(gpu.HotSpotTempC);

        HasVram = gpu.MemoryTotalMb is { } total && total > 0;
        if (HasVram)
        {
            var used = gpu.MemoryUsedMb ?? 0;
            VramPercent = Math.Clamp(used / gpu.MemoryTotalMb!.Value * 100, 0, 100);
            VramText = Strings.Gigabytes(used / 1024, gpu.MemoryTotalMb / 1024);
        }

        HasFan = gpu.FanPercent is not null || gpu.FanRpm is not null;
        FanText = HasFan
            ? (gpu.FanPercent is not null && gpu.FanRpm is not null
                ? $"{Strings.Percent(gpu.FanPercent)} · {Strings.Rpm(gpu.FanRpm)}"
                : gpu.FanPercent is not null ? Strings.Percent(gpu.FanPercent) : Strings.Rpm(gpu.FanRpm))
            : null;
        HasPower = gpu.PowerWatts is not null;
        PowerText = HasPower ? $"{gpu.PowerWatts:0} W" : null;
        HasClock = gpu.CoreClockMhz is not null;
        ClockText = HasClock ? $"{gpu.CoreClockMhz:0} MHz" : null;
    }
}

/// <summary>One drive card (NVMe / SSD / HDD).</summary>
public sealed partial class StorageItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name = Strings.Storage;
    [ObservableProperty] private string _kindText = Strings.Storage;
    [ObservableProperty] private string _tempText = Strings.NotAvailable;
    [ObservableProperty] private BccTone _tempTone = BccTone.Neutral;
    [ObservableProperty] private bool _hasTemp;
    [ObservableProperty] private bool _hasUsedSpace;
    [ObservableProperty] private double _usedSpace;
    [ObservableProperty] private string _usedSpaceText = Strings.NotAvailable;
    [ObservableProperty] private bool _hasRates;
    [ObservableProperty] private string _readText = string.Empty;
    [ObservableProperty] private string _writeText = string.Empty;
    [ObservableProperty] private bool _hasLife;
    [ObservableProperty] private string _lifeText = string.Empty;
    [ObservableProperty] private BccTone _lifeTone = BccTone.Neutral;
    [ObservableProperty] private bool _hasWritten;
    [ObservableProperty] private string _writtenText = string.Empty;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _hasNote;

    public StorageItemViewModel(string id) => Id = id;

    public string Id { get; }

    public void Update(StorageInfo drive)
    {
        Name = drive.Name;
        KindText = drive.Bus is { Length: > 0 } bus
            ? $"{Strings.Storage} · {bus}{(drive.IsHardDisk ? " HDD" : string.Empty)}"
            : Strings.Storage;

        HasTemp = drive.TemperatureC is not null;
        TempText = Strings.Celsius(drive.TemperatureC);
        TempTone = BccTones.ForStorageTemperature(drive.TemperatureC);

        HasUsedSpace = drive.UsedSpacePercent is not null;
        UsedSpace = Math.Clamp(drive.UsedSpacePercent ?? 0, 0, 100);
        UsedSpaceText = Strings.Percent(drive.UsedSpacePercent);

        HasRates = drive.ReadBytesPerSecond is not null || drive.WriteBytesPerSecond is not null;
        ReadText = Strings.ReadRate(drive.ReadBytesPerSecond);
        WriteText = Strings.WriteRate(drive.WriteBytesPerSecond);

        HasLife = drive.LifePercent is not null;
        LifeText = Strings.DriveHealth(drive.LifePercent);
        LifeTone = drive.LifePercent switch { null => BccTone.Neutral, >= 50 => BccTone.Success, >= 20 => BccTone.Warning, _ => BccTone.Danger };

        HasWritten = drive.DataWrittenGb is not null;
        WrittenText = Strings.DataWritten(drive.DataWrittenGb);

        ShowDetails = HasUsedSpace || HasRates || HasLife || HasWritten;
        Note = drive.IsHardDisk ? Strings.HardDiskNotPolled : !HasTemp && !ShowDetails ? Strings.StorageNoData : null;
        HasNote = Note is not null;
    }
}

/// <summary>One row of the 其他溫度 expander.</summary>
public sealed partial class TemperatureItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _group = string.Empty;
    [ObservableProperty] private string _valueText = Strings.NotAvailable;
    [ObservableProperty] private BccTone _tone = BccTone.Neutral;

    public TemperatureItem(TemperatureReading reading)
    {
        Id = reading.Id;
        Update(reading);
    }

    public string Id { get; }

    public void Update(TemperatureReading reading)
    {
        Name = reading.Name;
        Group = reading.Group;
        ValueText = Strings.Celsius(reading.ValueC);
        Tone = BccTones.ForTemperature(reading.ValueC);
    }
}
