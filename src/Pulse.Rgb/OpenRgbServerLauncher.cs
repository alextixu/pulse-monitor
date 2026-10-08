using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.Rgb;

public enum OpenRgbLaunchResult
{
    /// <summary>Something already answers on the SDK port (the user's own OpenRGB, or one started earlier).</summary>
    AlreadyRunning,
    /// <summary>The bundled OpenRGB was started and its SDK port is open.</summary>
    Started,
    /// <summary>No bundled OpenRGB next to Pulse.exe.</summary>
    NotBundled,
    /// <summary>The configured host is not this computer, so nothing can be launched.</summary>
    NotLocal,
    Failed,
}

/// <summary>
/// Starts the OpenRGB copy shipped in <c>&lt;Pulse folder&gt;\OpenRGB\</c> as a headless SDK server
/// (<c>--server --server-port N --noautoconnect</c>) when nothing listens on the SDK port yet, and stops it again with Pulse.
/// OpenRGB stays a separate GPL program: Pulse only runs it and talks to it over TCP.
/// On Windows the process is tied to Pulse with a kill-on-close job object, so it cannot outlive a crashed Pulse.
/// </summary>
public sealed class OpenRgbServerLauncher : IDisposable
{
    private static readonly TimeSpan PortWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<OpenRgbServerLauncher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private IntPtr _job;
    private bool _disposed;

    public OpenRgbServerLauncher(ILogger<OpenRgbServerLauncher>? logger = null)
    {
        _logger = logger ?? NullLogger<OpenRgbServerLauncher>.Instance;
    }

    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "OpenRGB");

    public static string BundledExecutable => Path.Combine(BundledDirectory, OperatingSystem.IsWindows() ? "OpenRGB.exe" : "openrgb");

    public static bool IsBundled => File.Exists(BundledExecutable);

    /// <summary>True while an OpenRGB process started by this launcher is running.</summary>
    public bool IsRunningBundled => _process is { HasExited: false };

    /// <summary>Extra command-line arguments (diagnostics / tests, e.g. "--nodetect").</summary>
    public string? AdditionalArguments { get; set; }

    public async Task<OpenRgbLaunchResult> EnsureRunningAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsPortOpenAsync(host, port, cancellationToken).ConfigureAwait(false)) return OpenRgbLaunchResult.AlreadyRunning;
            if (!IsLocal(host)) return OpenRgbLaunchResult.NotLocal;
            if (!IsBundled) return OpenRgbLaunchResult.NotBundled;

            if (!IsRunningBundled && !Start(port)) return OpenRgbLaunchResult.Failed;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < PortWaitTimeout)
            {
                if (_process is null || _process.HasExited)
                {
                    _logger.LogWarning("Bundled OpenRGB exited early (code {Code})", _process?.HasExited == true ? _process.ExitCode : -1);
                    return OpenRgbLaunchResult.Failed;
                }
                if (await IsPortOpenAsync(host, port, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Bundled OpenRGB SDK server is up on port {Port} after {Elapsed} ms", port, deadline.ElapsedMilliseconds);
                    return OpenRgbLaunchResult.Started;
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Bundled OpenRGB did not open port {Port} within {Timeout} s", port, PortWaitTimeout.TotalSeconds);
            return OpenRgbLaunchResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the OpenRGB process this launcher started (never one the user started).</summary>
    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                _logger.LogInformation("Stopped the bundled OpenRGB (pid {Pid})", process.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop the bundled OpenRGB");
        }
        finally
        {
            process.Dispose();
        }
    }

    private bool Start(int port)
    {
        var arguments = $"--server --server-port {port} --noautoconnect {AdditionalArguments}".Trim();
        try
        {
            var process = Process.Start(new ProcessStartInfo(BundledExecutable, arguments)
            {
                WorkingDirectory = BundledDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;

            _process = process;
            AttachToJob(process);
            _logger.LogInformation("Started bundled OpenRGB (pid {Pid}): {Exe} {Args}", process.Id, BundledExecutable, arguments);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not start the bundled OpenRGB {Exe}", BundledExecutable);
            return false;
        }
    }

    private static bool IsLocal(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Kill-on-close job: when Pulse exits for any reason (even a forced kill) Windows ends OpenRGB too.</summary>
    private void AttachToJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_job == IntPtr.Zero)
            {
                var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception();
                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref info, (uint)size))
                {
                    var error = new Win32Exception();
                    NativeMethods.CloseHandle(job);
                    throw error;
                }
                _job = job;
            }

            if (!NativeMethods.AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tie OpenRGB to Pulse's lifetime; it is still stopped on a normal exit");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        _gate.Dispose();
    }

    private static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
