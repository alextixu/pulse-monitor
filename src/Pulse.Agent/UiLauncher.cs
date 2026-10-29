using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pulse.Engine;

namespace Pulse.Agent;

/// <summary>
/// Opens the popover: through a connected Pulse.UI client when there is one (it shows or toggles itself), otherwise by
/// starting <c>Pulse.UI.exe --popover [--tab X]</c> next to Pulse.exe. The UI exits whenever the popover hides, so a tray
/// click that arrives right after the hide (the click itself deactivated the popover) must not reopen it: such a click is
/// the "close" half of a toggle (decisions-v2 §25). Window thread only.
/// </summary>
internal sealed class UiLauncher
{
    public const string UiExecutableName = "Pulse.UI.exe";

    /// <summary>A tray click this soon after the popover hid is the click that hid it.</summary>
    private static readonly TimeSpan ToggleCloseWindow = TimeSpan.FromMilliseconds(350);

    /// <summary>A cold start takes about a second before the new UI connects; do not start a second copy meanwhile.</summary>
    private static readonly TimeSpan LaunchGuard = TimeSpan.FromSeconds(3);

    private readonly AgentCore _agent;
    private readonly ILogger _log;
    private readonly string _executable;
    private long? _lastLaunchAt;
    private bool _missingLogged;

    public UiLauncher(AgentCore agent, ILogger log, string? executable = null)
    {
        _agent = agent;
        _log = log;
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, UiExecutableName);
    }

    /// <summary>Left click on the tray icon: toggle the popover.</summary>
    public void OnTrayClick()
    {
        DateTimeOffset? hiddenAt = null;
        try
        {
            hiddenAt = _agent.LastUiHiddenAt;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "LastUiHiddenAt unavailable");
        }

        if (hiddenAt is { } hidden && DateTimeOffset.UtcNow - hidden < ToggleCloseWindow)
        {
            _log.LogDebug("Tray click {Ms:0} ms after the popover hid: treated as its close", (DateTimeOffset.UtcNow - hidden).TotalMilliseconds);
            return;
        }

        Show(toggle: true, tab: null);
    }

    /// <summary>Shows the popover (optionally on <paramref name="tab"/>); <paramref name="toggle"/> hides an open one instead.</summary>
    public void Show(bool toggle, string? tab)
    {
        try
        {
            if (_agent.ShowPopoverInClient?.Invoke(toggle, tab) == true) return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not reach the connected popover; starting a new one");
        }

        Launch(tab);
    }

    private void Launch(string? tab)
    {
        var now = Environment.TickCount64;
        if (_lastLaunchAt is { } last && now - last < LaunchGuard.TotalMilliseconds)
        {
            _log.LogDebug("Popover launch already in progress; ignoring");
            return;
        }

        if (!File.Exists(_executable))
        {
            if (!_missingLogged) _log.LogWarning("Cannot open the popover: {Exe} not found", _executable);
            _missingLogged = true;
            return;
        }
        _missingLogged = false;

        // No shell: the popover inherits this process's token (an elevated agent gets an elevated UI, no UAC prompt).
        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_executable) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--popover");
        if (!string.IsNullOrWhiteSpace(tab))
        {
            startInfo.ArgumentList.Add("--tab");
            startInfo.ArgumentList.Add(tab);
        }

        try
        {
            using var process = Process.Start(startInfo);
            _lastLaunchAt = now;
            _log.LogInformation("Started {Exe} (pid {Pid}{Tab})", UiExecutableName, process?.Id, tab is null ? "" : ", tab " + tab);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Starting {Exe} failed", _executable);
        }
    }
}
