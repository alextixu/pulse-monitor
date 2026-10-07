using System.Text.RegularExpressions;
using Pulse.Core.Models;
using LibreHardwareMonitor.Hardware;

namespace Pulse.Hardware;

/// <summary>A fan sensor paired with the control channel (PWM duty) of the same hardware, when one exists.</summary>
internal sealed record FanBinding(ISensor Fan, ISensor? ControlSensor)
{
    public IControl? Control => ControlSensor?.Control;
}

internal sealed record SnapshotBuildResult(
    HardwareSnapshot Snapshot,
    Dictionary<string, FanBinding> FanBindings,
    bool CpuTemperatureAvailable,
    bool FanControlAvailable);

/// <summary>
/// Maps the LibreHardwareMonitor tree to <see cref="HardwareSnapshot"/> in a single traversal.
/// Pure mapping: the caller owns the LHM lock and has already updated the sensors.
/// </summary>
internal static partial class SnapshotBuilder
{
    private const string GroupMotherboard = "主機板";
    private const string GroupGpu = "顯示卡";
    private const string GroupController = "控制器";
    private const string GroupPsu = "電源";
    private const string GroupCpu = "處理器";
    private const string GroupMemory = "記憶體";
    private const string GroupStorage = "儲存裝置";
    private const string GroupOther = "其他";

    /// <summary>Per-core sensors: "CPU Core #n", "Core #n", and Alder Lake style "P-Core #n" / "E-Core #n".</summary>
    [GeneratedRegex(@"^(CPU |P-|E-)?Core #(\d+)$")]
    private static partial Regex CoreRegex();

    /// <summary>Per-logical-core load: "CPU Core #n" or "CPU Core #n Thread #m".</summary>
    [GeneratedRegex(@"^CPU Core #(\d+)(?: Thread #(\d+))?$")]
    private static partial Regex CoreLoadRegex();

    private const string PhysicalMemoryIdentifier = "/ram";

    public static SnapshotBuildResult Build(IEnumerable<IHardware> roots, IReadOnlyDictionary<string, double> manualFans)
    {
        CpuInfo? cpu = null;
        var cpuTemperatureAvailable = false;
        var gpus = new List<GpuInfo>();
        IHardware? memoryHardware = null;
        var fans = new List<FanInfo>();
        var otherTemperatures = new List<TemperatureReading>();
        var bindings = new Dictionary<string, FanBinding>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            var surfaced = false;
            switch (root.HardwareType)
            {
                case HardwareType.Cpu when cpu is null:
                    cpu = MapCpu(root, out cpuTemperatureAvailable);
                    surfaced = true;
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    gpus.Add(MapGpu(root));
                    surfaced = true;
                    break;
                case HardwareType.Memory:
                    // LHM lists "Virtual Memory" (/vram) before "Total Memory" (/ram); prefer physical RAM.
                    if (memoryHardware is null || root.Identifier.ToString() == PhysicalMemoryIdentifier)
                    {
                        memoryHardware = root;
                    }
                    break;
            }

            Walk(root, surfaced, manualFans, fans, otherTemperatures, bindings);
        }

        var snapshot = new HardwareSnapshot
        {
            Cpu = cpu,
            Gpus = gpus,
            Memory = memoryHardware is null ? null : MapMemory(memoryHardware),
            Fans = fans,
            OtherTemperatures = otherTemperatures,
            Timestamp = DateTimeOffset.Now,
        };

        var fanControlAvailable = false;
        foreach (var binding in bindings.Values)
        {
            if (binding.Control is not null)
            {
                fanControlAvailable = true;
                break;
            }
        }

        return new SnapshotBuildResult(snapshot, bindings, cpuTemperatureAvailable, fanControlAvailable);
    }

    /// <summary>Collects fans (with their control pairing) and non-CPU/GPU temperatures of a hardware node and its children.</summary>
    private static void Walk(
        IHardware hardware,
        bool surfaced,
        IReadOnlyDictionary<string, double> manualFans,
        List<FanInfo> fans,
        List<TemperatureReading> otherTemperatures,
        Dictionary<string, FanBinding> bindings)
    {
        var group = GroupFor(hardware.HardwareType);
        List<ISensor>? fanSensors = null;
        List<ISensor>? controlSensors = null;

        foreach (var sensor in hardware.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Fan:
                    (fanSensors ??= new List<ISensor>()).Add(sensor);
                    break;
                case SensorType.Control:
                    (controlSensors ??= new List<ISensor>()).Add(sensor);
                    break;
                // 0 °C is the "header not connected" sentinel of ASUS EC "T Sensor" inputs; skip it.
                case SensorType.Temperature when !surfaced && Value(sensor) is { } temp && temp > 0:
                    otherTemperatures.Add(new TemperatureReading(sensor.Identifier.ToString(), sensor.Name, group, temp));
                    break;
            }
        }

        if (fanSensors is not null)
        {
            foreach (var fan in fanSensors)
            {
                var control = FindControl(fan, controlSensors);
                var binding = new FanBinding(fan, control);
                var id = fan.Identifier.ToString();
                bindings[id] = binding;

                var ic = binding.Control;
                fans.Add(new FanInfo
                {
                    Id = id,
                    Name = fan.Name,
                    Group = group,
                    Rpm = Value(fan),
                    Percent = control is null ? null : Value(control),
                    CanControl = ic is not null,
                    IsManual = manualFans.ContainsKey(id),
                    MinPercent = ic?.MinSoftwareValue ?? 0,
                    MaxPercent = ic?.MaxSoftwareValue ?? 100,
                });
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            Walk(sub, surfaced, manualFans, fans, otherTemperatures, bindings);
        }
    }

    /// <summary>Control channel on the same hardware with the same index; falls back to the same name.</summary>
    private static ISensor? FindControl(ISensor fan, List<ISensor>? controls)
    {
        if (controls is null) return null;

        foreach (var control in controls)
        {
            if (control.Index == fan.Index) return control;
        }

        foreach (var control in controls)
        {
            if (string.Equals(control.Name, fan.Name, StringComparison.Ordinal)) return control;
        }

        return null;
    }

    private static CpuInfo MapCpu(IHardware cpu, out bool temperatureAvailable)
    {
        var loads = new List<ISensor>();
        var temps = new List<ISensor>();
        var clocks = new List<ISensor>();
        var powers = new List<ISensor>();
        temperatureAvailable = false;

        foreach (var sensor in cpu.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Load: loads.Add(sensor); break;
                case SensorType.Temperature:
                    temps.Add(sensor);
                    temperatureAvailable |= Value(sensor) is not null;
                    break;
                case SensorType.Clock: clocks.Add(sensor); break;
                case SensorType.Power: powers.Add(sensor); break;
            }
        }

        // Without the kernel driver LHM reports 0 W rather than null; a running CPU never draws 0 W.
        var power = FindValue(powers, "CPU Package", "Package");

        return new CpuInfo
        {
            Name = cpu.Name,
            LoadPercent = FindValue(loads, "CPU Total"),
            PackageTempC = FindValue(temps, "CPU Package", "Core (Tctl/Tdie)", "Core Average", "CPU Core"),
            CoreTempsC = OrderedValues(temps, CoreRegex(), CoreOrder),
            CoreLoadsPercent = OrderedValues(loads, CoreLoadRegex(), CoreThreadOrder),
            ClockMhz = FindValue(clocks, "CPU Core #1", "P-Core #1") ?? MaxValue(clocks, CoreRegex()),
            PowerWatts = power > 0 ? power : null,
        };
    }

    /// <summary>P-cores (and plain cores) before E-cores, then by core number.</summary>
    private static (int, int) CoreOrder(Match m)
        => (m.Groups[1].ValueSpan.SequenceEqual("E-") ? 1 : 0, int.Parse(m.Groups[2].ValueSpan));

    private static (int, int) CoreThreadOrder(Match m)
        => (int.Parse(m.Groups[1].ValueSpan), m.Groups[2].Success ? int.Parse(m.Groups[2].ValueSpan) : 0);

    private static GpuInfo MapGpu(IHardware gpu)
    {
        var loads = new List<ISensor>();
        var temps = new List<ISensor>();
        var datas = new List<ISensor>();
        var controls = new List<ISensor>();
        var fans = new List<ISensor>();
        var powers = new List<ISensor>();
        var clocks = new List<ISensor>();

        foreach (var sensor in gpu.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Load: loads.Add(sensor); break;
                case SensorType.Temperature: temps.Add(sensor); break;
                case SensorType.SmallData or SensorType.Data: datas.Add(sensor); break;
                case SensorType.Control: controls.Add(sensor); break;
                case SensorType.Fan: fans.Add(sensor); break;
                case SensorType.Power: powers.Add(sensor); break;
                case SensorType.Clock: clocks.Add(sensor); break;
            }
        }

        return new GpuInfo
        {
            Id = gpu.Identifier.ToString(),
            Name = gpu.Name,
            Vendor = gpu.HardwareType switch
            {
                HardwareType.GpuNvidia => "NVIDIA",
                HardwareType.GpuAmd => "AMD",
                HardwareType.GpuIntel => "Intel",
                _ => "Unknown",
            },
            LoadPercent = FindValue(loads, "GPU Core"),
            CoreTempC = FindValue(temps, "GPU Core"),
            HotSpotTempC = FindValue(temps, "GPU Hot Spot"),
            MemoryTempC = FindValue(temps, "GPU Memory", "GPU Memory Junction"),
            MemoryUsedMb = MegabytesOf(Find(datas, "GPU Memory Used", "D3D Dedicated Memory Used")),
            MemoryTotalMb = MegabytesOf(Find(datas, "GPU Memory Total", "D3D Dedicated Memory Total")),
            FanPercent = FindValue(controls, "GPU Fan", "GPU Fan 1") ?? FirstValue(controls),
            FanRpm = FindValue(fans, "GPU Fan", "GPU Fan 1") ?? FirstValue(fans),
            PowerWatts = FindValue(powers, "GPU Package", "GPU Power"),
            CoreClockMhz = FindValue(clocks, "GPU Core"),
            MemoryClockMhz = FindValue(clocks, "GPU Memory"),
        };
    }

    private static MemoryInfo MapMemory(IHardware memory)
    {
        var loads = new List<ISensor>();
        var datas = new List<ISensor>();

        foreach (var sensor in memory.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Load: loads.Add(sensor); break;
                case SensorType.Data: datas.Add(sensor); break;
            }
        }

        var used = FindValue(datas, "Memory Used");
        var available = FindValue(datas, "Memory Available");

        return new MemoryInfo
        {
            UsedGb = used,
            TotalGb = used is { } u && available is { } a ? u + a : null,
            LoadPercent = FindValue(loads, "Memory"),
        };
    }

    private static string GroupFor(HardwareType type) => type switch
    {
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => GroupMotherboard,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => GroupGpu,
        HardwareType.Cooler => GroupController,
        HardwareType.Psu => GroupPsu,
        HardwareType.Cpu => GroupCpu,
        HardwareType.Memory => GroupMemory,
        HardwareType.Storage => GroupStorage,
        _ => GroupOther,
    };

    /// <summary>Current value as double, or null when the sensor has not produced a (finite) reading.</summary>
    private static double? Value(ISensor sensor)
        => sensor.Value is { } v && float.IsFinite(v) ? v : null;

    /// <summary>First sensor, in priority order of <paramref name="names"/>, that currently has a value.</summary>
    private static ISensor? Find(List<ISensor> sensors, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var sensor in sensors)
            {
                if (string.Equals(sensor.Name, name, StringComparison.Ordinal) && Value(sensor) is not null)
                {
                    return sensor;
                }
            }
        }

        return null;
    }

    private static double? FindValue(List<ISensor> sensors, params string[] names)
        => Find(sensors, names) is { } s ? Value(s) : null;

    private static double? FirstValue(List<ISensor> sensors)
    {
        foreach (var sensor in sensors)
        {
            if (Value(sensor) is { } v) return v;
        }

        return null;
    }

    /// <summary>SmallData sensors report MB, Data sensors report GB.</summary>
    private static double? MegabytesOf(ISensor? sensor)
        => sensor is null ? null
            : sensor.SensorType == SensorType.Data ? Value(sensor) * 1024
            : Value(sensor);

    /// <summary>Values of sensors whose name matches <paramref name="pattern"/>, ordered by the key <paramref name="order"/> derives from the match.</summary>
    private static double?[] OrderedValues(List<ISensor> sensors, Regex pattern, Func<Match, (int Major, int Minor)> order)
    {
        List<(int Major, int Minor, double? Value)>? matches = null;

        foreach (var sensor in sensors)
        {
            var m = pattern.Match(sensor.Name);
            if (!m.Success) continue;

            var (major, minor) = order(m);
            (matches ??= new List<(int, int, double?)>()).Add((major, minor, Value(sensor)));
        }

        if (matches is null) return Array.Empty<double?>();

        matches.Sort(static (a, b) =>
        {
            var c = a.Major.CompareTo(b.Major);
            return c != 0 ? c : a.Minor.CompareTo(b.Minor);
        });

        var result = new double?[matches.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = matches[i].Value;
        }

        return result;
    }

    private static double? MaxValue(List<ISensor> sensors, Regex pattern)
    {
        double? max = null;
        foreach (var sensor in sensors)
        {
            if (!pattern.IsMatch(sensor.Name)) continue;
            if (Value(sensor) is { } v && (max is null || v > max)) max = v;
        }

        return max;
    }
}
