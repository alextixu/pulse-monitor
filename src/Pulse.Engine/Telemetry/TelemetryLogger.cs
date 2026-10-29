using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pulse.Core.FanControl;
using Pulse.Core.Models;

namespace Pulse.Engine.Telemetry;

/// <summary>
/// Writes one CSV line per published hardware frame (1 s while a popover is visible, 2 s otherwise) to
/// <c>&lt;directory&gt;\telemetry.csv</c>: CPU / GPU / memory load, temperatures, power and clocks, every detected fan
/// (RPM, duty, fan-mode state), the other temperatures and the drives. At each new clock hour the file is moved to
/// telemetry-previous.csv (replacing the older one) and a fresh file is started, so at most about two hours are kept.
/// The column set is fixed per file from the 4th sample (fan presence has settled by then; the first samples are
/// buffered, not lost). UTF-8 with BOM so Excel shows the Chinese names; shared for reading so it can be opened live.
/// Rows are formatted and written on a pool task; the engine only enqueues.
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    /// <summary>Samples collected before the column set is fixed, so fan-presence detection (3 samples) has settled.</summary>
    private const int WarmupSamples = 4;

    private readonly ILogger _log;
    private readonly Channel<Sample> _queue = Channel.CreateBounded<Sample>(
        new BoundedChannelOptions(120) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<Sample> _warmup = new();
    private readonly Task _writerTask;

    private StreamWriter? _writer;
    private DateTime _fileHour;
    private List<Column>? _columns;
    private int _disposed;

    public TelemetryLogger(string directory, ILogger log)
    {
        Directory = directory;
        _log = log;
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public string Directory { get; }

    public string CurrentPath => Path.Combine(Directory, "telemetry.csv");

    public string PreviousPath => Path.Combine(Directory, "telemetry-previous.csv");

    public static string FileNameIn(string directory) => Path.Combine(directory, "telemetry.csv");

    /// <summary>Queues one row (dropped when the snapshot has neither CPU nor GPU data). Any thread, never blocks.</summary>
    public void Enqueue(DateTime time, HardwareSnapshot snapshot, FanModeResult fans, HardwareMonitorState state)
    {
        if (Volatile.Read(ref _disposed) != 0 || snapshot.Cpu is null && snapshot.Gpus.Count == 0) return;
        _queue.Writer.TryWrite(new Sample(time, snapshot, fans, state));
    }

    /// <summary>Writes what is queued and closes the file (bounded wait). Idempotent.</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return _writerTask.IsCompleted;
        _queue.Writer.TryComplete();
        try
        {
            return _writerTask.Wait(timeout);
        }
        catch
        {
            return true; // the loop swallows its own failures; nothing else to do while shutting down
        }
    }

    public void Dispose() => Flush(TimeSpan.FromSeconds(2));

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
        var row = new Row(sample);
        var sb = new StringBuilder(_columns.Count * 6);
        for (var i = 0; i < _columns.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(_columns[i].Value(row)));
        }
        _writer.WriteLine(sb.ToString());
    }

    /// <summary>Fixed column set for one file, derived from a settled sample (hidden fan headers are left out).</summary>
    private static List<Column> BuildColumns(Sample reference)
    {
        var columns = new List<Column>
        {
            new("時間", r => r.Sample.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new("硬體狀態", r => r.Sample.State.ToString()),
            new("風扇模式", r => r.Sample.Fans.Mode.ToString()),
            new("CPU 使用率 %", r => N(r.Snapshot.Cpu?.LoadPercent)),
            new("CPU 溫度 °C", r => N(r.Snapshot.Cpu?.PackageTempC)),
            new("CPU 最高核心溫度 °C", r => N(r.Snapshot.Cpu?.CoreTempsC.Where(t => t is not null).Max())),
            new("CPU 時脈 MHz", r => N(r.Snapshot.Cpu?.ClockMhz, "0")),
            new("CPU 功耗 W", r => N(r.Snapshot.Cpu?.PowerWatts)),
        };

        var gpuCount = reference.Snapshot.Gpus.Count;
        for (var i = 0; i < gpuCount; i++)
        {
            var index = i;
            var prefix = gpuCount > 1 ? $"GPU{index + 1}" : "GPU";
            GpuInfo? Gpu(Row r) => index < r.Snapshot.Gpus.Count ? r.Snapshot.Gpus[index] : null;
            columns.AddRange(new Column[]
            {
                new($"{prefix} 使用率 %", r => N(Gpu(r)?.LoadPercent)),
                new($"{prefix} 溫度 °C", r => N(Gpu(r)?.CoreTempC)),
                new($"{prefix} 熱點 °C", r => N(Gpu(r)?.HotSpotTempC)),
                new($"{prefix} 顯存溫度 °C", r => N(Gpu(r)?.MemoryTempC)),
                new($"{prefix} 功耗 W", r => N(Gpu(r)?.PowerWatts)),
                new($"{prefix} 核心時脈 MHz", r => N(Gpu(r)?.CoreClockMhz, "0")),
                new($"{prefix} 顯存使用 MB", r => N(Gpu(r)?.MemoryUsedMb, "0")),
            });
        }

        columns.Add(new("記憶體使用 GB", r => N(r.Snapshot.Memory?.UsedGb, "0.00")));
        columns.Add(new("記憶體使用率 %", r => N(r.Snapshot.Memory?.LoadPercent)));

        foreach (var fan in reference.Snapshot.Fans)
        {
            if (reference.Fans.Fans.TryGetValue(fan.Id, out var status) && status.Presence == FanPresence.Hidden) continue;
            var id = fan.Id;
            var name = $"{fan.Group} {fan.Name}";
            columns.Add(new($"{name} RPM", r => N(r.Fan(id)?.Rpm, "0")));
            columns.Add(new($"{name} 轉速 %", r => N(r.Fan(id)?.Percent, "0")));
            columns.Add(new($"{name} 狀態", r => r.Sample.Fans.Fans.TryGetValue(id, out var st) ? st.State.ToString() : (r.Fan(id)?.IsManual == true ? "Manual" : "")));
        }

        foreach (var temp in reference.Snapshot.OtherTemperatures)
        {
            var id = temp.Id;
            columns.Add(new($"{temp.Group} {temp.Name} °C", r => N(r.Temperature(id)?.ValueC)));
        }

        // Drives (hard disks are never polled, so they would only add empty columns).
        foreach (var drive in reference.Snapshot.Storages.Where(d => !d.IsHardDisk))
        {
            var id = drive.Id;
            var name = $"儲存裝置 {drive.Name}";
            columns.Add(new($"{name} °C", r => N(r.Drive(id)?.TemperatureC)));
            columns.Add(new($"{name} 讀取 MB/s", r => N(r.Drive(id)?.ReadBytesPerSecond / 1024 / 1024, "0.0")));
            columns.Add(new($"{name} 寫入 MB/s", r => N(r.Drive(id)?.WriteBytesPerSecond / 1024 / 1024, "0.0")));
        }

        return columns;
    }

    private static string N(double? value, string format = "0.0")
        => value is { } v && !double.IsNaN(v) ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private sealed record Sample(DateTime Time, HardwareSnapshot Snapshot, FanModeResult Fans, HardwareMonitorState State);

    /// <summary>One row being formatted: the fans / temperatures / drives indexed once instead of searched per cell.</summary>
    private sealed class Row
    {
        private Dictionary<string, FanInfo>? _fans;
        private Dictionary<string, TemperatureReading>? _temps;
        private Dictionary<string, StorageInfo>? _drives;

        public Row(Sample sample) => Sample = sample;

        public Sample Sample { get; }
        public HardwareSnapshot Snapshot => Sample.Snapshot;

        public FanInfo? Fan(string id) => (_fans ??= Index(Snapshot.Fans, f => f.Id)).GetValueOrDefault(id);
        public TemperatureReading? Temperature(string id) => (_temps ??= Index(Snapshot.OtherTemperatures, t => t.Id)).GetValueOrDefault(id);
        public StorageInfo? Drive(string id) => (_drives ??= Index(Snapshot.Storages, d => d.Id)).GetValueOrDefault(id);

        private static Dictionary<string, T> Index<T>(IReadOnlyList<T> items, Func<T, string> key)
        {
            // First occurrence wins, like the FirstOrDefault lookups this replaces.
            var map = new Dictionary<string, T>(items.Count, StringComparer.Ordinal);
            foreach (var item in items) map.TryAdd(key(item), item);
            return map;
        }
    }

    private sealed record Column(string Header, Func<Row, string> Value);
}
