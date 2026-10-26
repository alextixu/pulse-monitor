using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Pulse.Core.FanControl;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Writes one CSV line per hardware snapshot (every second while enabled) to %LOCALAPPDATA%\Pulse\logs\telemetry.csv:
/// CPU / GPU / memory load, temperatures, power and clocks, every detected fan (RPM, duty, fan-mode state) and the
/// motherboard temperatures. At each new clock hour the file is moved to telemetry-previous.csv (replacing the older one)
/// and a fresh file is started, so at most about two hours are kept on disk.
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    /// <summary>Samples collected before the column set is fixed, so fan-presence detection (3 samples) has settled.</summary>
    private const int WarmupSamples = 4;

    private readonly MonitoringService _monitoring;
    private readonly FanModeService _fanModes;
    private readonly SettingsService _settings;
    private readonly ILogger<TelemetryLogger> _log;
    private readonly Channel<Sample> _queue = Channel.CreateBounded<Sample>(
        new BoundedChannelOptions(120) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<Sample> _warmup = new();

    private FanModeResult _fanResult = FanModeResult.Empty;
    private Task? _writerTask;
    private StreamWriter? _writer;
    private DateTime _fileHour;
    private List<Column>? _columns;
    private bool _started;
    private bool _disposed;

    public TelemetryLogger(MonitoringService monitoring, FanModeService fanModes, SettingsService settings, ILogger<TelemetryLogger> log)
    {
        _monitoring = monitoring;
        _fanModes = fanModes;
        _settings = settings;
        _log = log;
    }

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "logs");

    public static string CurrentPath => Path.Combine(Directory, "telemetry.csv");

    public static string PreviousPath => Path.Combine(Directory, "telemetry-previous.csv");

    public void Start()
    {
        if (_started) return;
        _started = true;
        _monitoring.HardwareUpdated += OnHardwareUpdated;
        _fanModes.ResultChanged += OnFanResultChanged;
        _writerTask = Task.Run(WriterLoopAsync);
        _log.LogInformation("Telemetry log {State}: {Path}", _settings.Settings.TelemetryEnabled ? "enabled" : "disabled", CurrentPath);
    }

    private void OnFanResultChanged(object? sender, FanModeResult result) => _fanResult = result;

    private void OnHardwareUpdated(object? sender, HardwareSnapshot snapshot)
    {
        if (_disposed || !_settings.Settings.TelemetryEnabled || snapshot.Cpu is null && snapshot.Gpus.Count == 0) return;
        _queue.Writer.TryWrite(new Sample(DateTime.Now, snapshot, _fanResult, _monitoring.HardwareStatus.State));
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var sample in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Write(sample);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Telemetry write failed; reopening the file on the next sample");
                    CloseFile();
                }
            }
        }
        finally
        {
            CloseFile();
        }
    }

    private void Write(Sample sample)
    {
        var hour = new DateTime(sample.Time.Year, sample.Time.Month, sample.Time.Day, sample.Time.Hour, 0, 0);
        if (_columns is not null && hour != _fileHour) Rotate();

        if (_columns is null)
        {
            _warmup.Add(sample);
            if (_warmup.Count < WarmupSamples) return;
            _columns = BuildColumns(sample);
            OpenFile(hour);
            foreach (var buffered in _warmup) WriteLine(buffered);
            _warmup.Clear();
            return;
        }

        if (_writer is null)
        {
            // Reopen after a write failure: keep appending to the current hour's file.
            var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _fileHour = hour;
        }
        WriteLine(sample);
    }

    /// <summary>Hourly clear: the finished hour becomes telemetry-previous.csv (moved by <see cref="OpenFile"/>), columns are rebuilt.</summary>
    private void Rotate()
    {
        CloseFile();
        _columns = null;
        _log.LogInformation("Telemetry log rotated (previous hour kept in {Path})", PreviousPath);
    }

    /// <summary>Starts a new file (a file left by an earlier run is kept as telemetry-previous.csv). Requires <see cref="_columns"/>.</summary>
    private void OpenFile(DateTime hour)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (File.Exists(CurrentPath))
        {
            try { File.Move(CurrentPath, PreviousPath, overwrite: true); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not move the earlier telemetry file aside"); }
        }

        var stream = new FileStream(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        // UTF-8 with BOM so Excel shows the Chinese fan / sensor names correctly.
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        _fileHour = hour;
        _writer.WriteLine(string.Join(',', _columns!.Select(c => Escape(c.Header))));
    }

    private void CloseFile()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }

    private void WriteLine(Sample sample)
    {
        if (_writer is null || _columns is null) return;
        _writer.WriteLine(string.Join(',', _columns.Select(c => Escape(c.Value(sample)))));
    }

    /// <summary>Fixed column set for one file, derived from a settled sample (hidden fan headers are left out).</summary>
    private static List<Column> BuildColumns(Sample reference)
    {
        var columns = new List<Column>
        {
            new("時間", s => s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new("硬體狀態", s => s.State.ToString()),
            new("風扇模式", s => s.Fans.Mode.ToString()),
            new("CPU 使用率 %", s => N(s.Snapshot.Cpu?.LoadPercent)),
            new("CPU 溫度 °C", s => N(s.Snapshot.Cpu?.PackageTempC)),
            new("CPU 最高核心溫度 °C", s => N(s.Snapshot.Cpu?.CoreTempsC.Where(t => t is not null).Max())),
            new("CPU 時脈 MHz", s => N(s.Snapshot.Cpu?.ClockMhz, "0")),
            new("CPU 功耗 W", s => N(s.Snapshot.Cpu?.PowerWatts)),
        };

        for (var i = 0; i < reference.Snapshot.Gpus.Count; i++)
        {
            var index = i;
            var prefix = reference.Snapshot.Gpus.Count > 1 ? $"GPU{index + 1}" : "GPU";
            GpuInfo? Gpu(Sample s) => index < s.Snapshot.Gpus.Count ? s.Snapshot.Gpus[index] : null;
            columns.AddRange(new Column[]
            {
                new($"{prefix} 使用率 %", s => N(Gpu(s)?.LoadPercent)),
                new($"{prefix} 溫度 °C", s => N(Gpu(s)?.CoreTempC)),
                new($"{prefix} 熱點 °C", s => N(Gpu(s)?.HotSpotTempC)),
                new($"{prefix} 顯存溫度 °C", s => N(Gpu(s)?.MemoryTempC)),
                new($"{prefix} 功耗 W", s => N(Gpu(s)?.PowerWatts)),
                new($"{prefix} 核心時脈 MHz", s => N(Gpu(s)?.CoreClockMhz, "0")),
                new($"{prefix} 顯存使用 MB", s => N(Gpu(s)?.MemoryUsedMb, "0")),
            });
        }

        columns.Add(new("記憶體使用 GB", s => N(s.Snapshot.Memory?.UsedGb, "0.00")));
        columns.Add(new("記憶體使用率 %", s => N(s.Snapshot.Memory?.LoadPercent)));

        foreach (var fan in reference.Snapshot.Fans)
        {
            if (reference.Fans.Fans.TryGetValue(fan.Id, out var status) && status.Presence == FanPresence.Hidden) continue;
            var id = fan.Id;
            var name = $"{fan.Group} {fan.Name}";
            FanInfo? Fan(Sample s) => s.Snapshot.Fans.FirstOrDefault(f => f.Id == id);
            columns.Add(new($"{name} RPM", s => N(Fan(s)?.Rpm, "0")));
            columns.Add(new($"{name} 轉速 %", s => N(Fan(s)?.Percent, "0")));
            columns.Add(new($"{name} 狀態", s => s.Fans.Fans.TryGetValue(id, out var st) ? st.State.ToString() : (Fan(s)?.IsManual == true ? "Manual" : "")));
        }

        foreach (var temp in reference.Snapshot.OtherTemperatures)
        {
            var id = temp.Id;
            columns.Add(new($"{temp.Group} {temp.Name} °C", s => N(s.Snapshot.OtherTemperatures.FirstOrDefault(t => t.Id == id)?.ValueC)));
        }

        // Drives (hard disks are never polled, so they would only add empty columns).
        foreach (var drive in reference.Snapshot.Storages.Where(d => !d.IsHardDisk))
        {
            var id = drive.Id;
            StorageInfo? Drive(Sample s) => s.Snapshot.Storages.FirstOrDefault(d => d.Id == id);
            var name = $"儲存裝置 {drive.Name}";
            columns.Add(new($"{name} °C", s => N(Drive(s)?.TemperatureC)));
            columns.Add(new($"{name} 讀取 MB/s", s => N(Drive(s)?.ReadBytesPerSecond / 1024 / 1024, "0.0")));
            columns.Add(new($"{name} 寫入 MB/s", s => N(Drive(s)?.WriteBytesPerSecond / 1024 / 1024, "0.0")));
        }

        return columns;
    }

    private static string N(double? value, string format = "0.0")
        => value is { } v && !double.IsNaN(v) ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            _monitoring.HardwareUpdated -= OnHardwareUpdated;
            _fanModes.ResultChanged -= OnFanResultChanged;
        }
        _queue.Writer.TryComplete();
        try { _writerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        CloseFile();
    }

    private sealed record Sample(DateTime Time, HardwareSnapshot Snapshot, FanModeResult Fans, HardwareMonitorState State);

    private sealed record Column(string Header, Func<Sample, string> Value);
}
