using System.Text;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Minimal thread-safe file logger: one line per entry, exception on the following lines,
/// size-based roll (app.log → app.1.log) when the file exceeds <see cref="MaxBytes"/>.
/// The file stays open between lines (shared for reading, so it can be tailed live) and every line is flushed.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _rolledPath;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        _path = path;
        _rolledPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch
        {
            // Logging must never take the app down; writes will fail silently below.
        }
    }

    public string FilePath => _path;

    /// <summary>Default location: %LOCALAPPDATA%\Pulse\logs\app.log.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "logs", "app.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_writer is null || _stream is null) Open();
                else if (_stream.Length >= MaxBytes) Roll();
                _writer!.Write(line);
                _writer.Flush();
            }
            catch
            {
                // Swallow: a failing log sink must not crash the app. Reopen on the next line.
                CloseLocked();
            }
        }
    }

    private void Open()
    {
        // Another process (an older Pulse, or the other Pulse executable) may also append: share writes and deletes.
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false));
        if (_stream.Length >= MaxBytes) Roll();
    }

    private void Roll()
    {
        CloseLocked();
        try
        {
            if (File.Exists(_rolledPath)) File.Delete(_rolledPath);
            File.Move(_path, _rolledPath);
        }
        catch
        {
            // Keep appending to the current file if rolling fails (e.g. locked by another process).
        }
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false));
    }

    private void CloseLocked()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _stream = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseLocked();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
              .Append(_category).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }
            sb.AppendLine();
            _provider.Write(sb.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
