using Pulse.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Pulse.Windows;

/// <summary>Launch-at-login via HKCU\...\CurrentVersion\Run. The stored command is <c>"&lt;exe&gt;" --minimized</c>.</summary>
public sealed class WindowsAutoStartService : IAutoStartService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Pulse";
    public const string MinimizedArgument = "--minimized";

    private readonly ILogger<WindowsAutoStartService> _logger;
    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly string? _executablePath;

    public WindowsAutoStartService(ILogger<WindowsAutoStartService> logger)
        : this(logger, RunKeyPath, ValueName, Environment.ProcessPath)
    {
    }

    /// <summary>Test seam: lets a harness point at a scratch key / fake exe instead of the real Run key.</summary>
    public WindowsAutoStartService(ILogger<WindowsAutoStartService> logger, string keyPath, string valueName, string? executablePath)
    {
        _logger = logger;
        _keyPath = keyPath;
        _valueName = valueName;
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
    }

    public bool IsSupported => OperatingSystem.IsWindows() && _executablePath is not null;

    public bool IsEnabled
    {
        get
        {
            if (!IsSupported) return false;
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
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            _logger.LogDebug("Auto-start not supported in this environment; ignoring SetEnabled({Enabled})", enabled);
            return;
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
            // Callers re-read IsEnabled to reflect the actual state; a policy-locked Run key must not crash the UI.
            _logger.LogWarning(ex, "Failed to {Action} auto-start registration", enabled ? "enable" : "disable");
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
