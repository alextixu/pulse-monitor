using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Pulse.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Pulse.Windows;

/// <summary>UAC elevation: detects the Administrators token and re-launches the current exe with the "runas" verb.</summary>
public sealed class WindowsElevationService : IElevationService
{
    /// <summary>Appended to the original arguments so the new instance knows it was spawned by <see cref="RelaunchElevated"/>.</summary>
    public const string ElevatedRelaunchArgument = "--elevated-relaunch";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: user dismissed the UAC prompt

    private readonly ILogger<WindowsElevationService> _logger;
    private readonly Lazy<bool> _isElevated;

    public WindowsElevationService(ILogger<WindowsElevationService> logger)
    {
        _logger = logger;
        _isElevated = new Lazy<bool>(DetectElevation, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsElevated => _isElevated.Value;

    public bool CanElevate => OperatingSystem.IsWindows();

    public bool RelaunchElevated()
    {
        if (!CanElevate) return false;
        if (IsElevated)
        {
            _logger.LogInformation("RelaunchElevated called while already elevated; nothing to do");
            return false;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _logger.LogWarning("Cannot relaunch elevated: Environment.ProcessPath is unavailable");
            return false;
        }

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (!string.Equals(arg, ElevatedRelaunchArgument, StringComparison.Ordinal)) startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add(ElevatedRelaunchArgument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("Process.Start returned null when relaunching elevated");
                return false;
            }

            _logger.LogInformation("Started elevated instance (pid {Pid})", process.Id);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.LogInformation("Elevation cancelled by the user");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to relaunch elevated");
            return false;
        }
    }

    private bool DetectElevation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine elevation state; assuming not elevated");
            return false;
        }
    }
}
