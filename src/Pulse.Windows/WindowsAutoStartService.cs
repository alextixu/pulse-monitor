using System.Diagnostics;
using Pulse.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Pulse.Windows;

/// <summary>
/// Launch at login. Two mechanisms exist: the HKCU Run value <c>"&lt;exe&gt;" --minimized</c> written by this service, and the
/// scheduled task "Pulse" registered by tools\install-pulse.ps1 (elevated at logon, no UAC prompt). Auto-start counts as
/// enabled when either is present. When the task exists the Run value is redundant and harmful — whichever copy starts
/// first wins the single-instance mutex, so the unelevated Run copy could leave Pulse unelevated (seen 7/7 boots) — so
/// enabling removes the Run value and disabling deletes the task as well (decisions-v2 §23).
/// </summary>
public sealed class WindowsAutoStartService : IAutoStartService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Pulse";
    public const string MinimizedArgument = "--minimized";
    /// <summary>Name of the logon task created by tools\install-pulse.ps1.</summary>
    public const string TaskName = "Pulse";

    private readonly ILogger<WindowsAutoStartService> _logger;
    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly string? _executablePath;
    private readonly IAutoStartTask? _task;

    public WindowsAutoStartService(ILogger<WindowsAutoStartService> logger)
        : this(logger, RunKeyPath, ValueName, Environment.ProcessPath, new SchtasksAutoStartTask(TaskName, logger))
    {
    }

    /// <summary>
    /// Test seam: lets a harness point at a scratch key / fake exe instead of the real Run key. Never looks at (or deletes)
    /// the real scheduled task.
    /// </summary>
    public WindowsAutoStartService(ILogger<WindowsAutoStartService> logger, string keyPath, string valueName, string? executablePath)
        : this(logger, keyPath, valueName, executablePath, task: null)
    {
    }

    /// <summary>Test seam with a fake scheduled task (null = the Run value only).</summary>
    public WindowsAutoStartService(ILogger<WindowsAutoStartService> logger, string keyPath, string valueName, string? executablePath, IAutoStartTask? task)
    {
        _logger = logger;
        _keyPath = keyPath;
        _valueName = valueName;
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
        _task = task;
    }

    public bool IsSupported => OperatingSystem.IsWindows() && _executablePath is not null;

    /// <summary>The Run value points at this executable, or the scheduled task exists.</summary>
    public bool IsEnabled => IsSupported && (RunValueMatches() || TaskExists());

    /// <summary>Auto-start comes from the scheduled task "Pulse" (it exists), whatever the Run value says.</summary>
    public bool IsViaTask => IsSupported && TaskExists();

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            _logger.LogDebug("Auto-start not supported in this environment; ignoring SetEnabled({Enabled})", enabled);
            return;
        }

        var viaTask = TaskExists();
        if (enabled && viaTask)
        {
            // The task already starts Pulse (elevated); a Run value next to it would only start a second, unelevated copy.
            DeleteRunValue("auto-start comes from the scheduled task");
            return;
        }

        if (!enabled && viaTask)
        {
            if (_task!.Delete()) _logger.LogInformation("Scheduled task {Task} deleted", TaskName);
            else _logger.LogWarning("Could not delete the scheduled task {Task} (needs administrator rights)", TaskName);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (enabled)
            {
                key.SetValue(_valueName, $"\"{_executablePath}\" {MinimizedArgument}", RegistryValueKind.String);
                _logger.LogInformation("Auto-start enabled for {Exe}", _executablePath);
            }
            else
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
                _logger.LogInformation("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            // Callers re-read IsEnabled to reflect the actual state; a policy-locked Run key must not crash the agent.
            _logger.LogWarning(ex, "Failed to {Action} auto-start registration", enabled ? "enable" : "disable");
        }
    }

    private bool TaskExists()
    {
        if (_task is null) return false;
        try
        {
            return _task.Exists();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query the scheduled task {Task}", TaskName);
            return false;
        }
    }

    private bool RunValueMatches()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            if (key?.GetValue(_valueName) is not string command) return false;
            var storedExe = ExtractExecutable(command);
            return storedExe is not null && PathsEqual(storedExe, _executablePath!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read auto-start registration");
            return false;
        }
    }

    private void DeleteRunValue(string why)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            if (key?.GetValue(_valueName) is null) return;
            key.DeleteValue(_valueName, throwOnMissingValue: false);
            _logger.LogInformation("Removed the Run value {Value}: {Why}", _valueName, why);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove the Run value {Value}", _valueName);
        }
    }

    /// <summary>"\"C:\path\app.exe\" --minimized" or "C:\path\app.exe --minimized" → "C:\path\app.exe".</summary>
    internal static string? ExtractExecutable(string command)
    {
        var s = command.Trim();
        if (s.Length == 0) return null;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>The logon task that starts Pulse (seam for <see cref="WindowsAutoStartService"/>).</summary>
public interface IAutoStartTask
{
    bool Exists();

    /// <summary>Deletes the task; false when it failed (deleting an elevated task needs administrator rights).</summary>
    bool Delete();
}

/// <summary>Scheduled task via schtasks.exe from System32 (exit code 0 = exists / deleted).</summary>
public sealed class SchtasksAutoStartTask : IAutoStartTask
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _taskName;
    private readonly ILogger _logger;

    public SchtasksAutoStartTask(string taskName, ILogger? logger = null)
    {
        _taskName = taskName;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool Exists() => Run("/query", "/tn", _taskName) == 0;

    public bool Delete() => Run("/delete", "/tn", _taskName, "/f") == 0;

    private int Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null) return -1;
        // Drain both pipes so a chatty schtasks cannot block on a full buffer.
        _ = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            _logger.LogWarning("schtasks {Args} did not finish within {Seconds} s", string.Join(' ', arguments), Timeout.TotalSeconds);
            try { process.Kill(); } catch { /* already gone */ }
            return -1;
        }
        process.WaitForExit(); // flushes the redirected streams
        if (process.ExitCode != 0 && arguments[0] != "/query")
        {
            _logger.LogDebug("schtasks {Args} exited {Code}: {Error}", string.Join(' ', arguments), process.ExitCode, error.IsCompleted ? error.Result.Trim() : "");
        }
        return process.ExitCode;
    }
}
