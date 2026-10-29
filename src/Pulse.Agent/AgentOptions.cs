namespace Pulse.Agent;

/// <summary>Parsed command line of Pulse.exe. Case-insensitive; unknown arguments are ignored.</summary>
public sealed record AgentOptions
{
    public const string MinimizedArgument = "--minimized";
    public const string ShowUiArgument = "--show-ui";

    /// <summary>"--minimized" (also "-m"): tray only, do not open the popover at start (the scheduled task and the Run key pass it).</summary>
    public bool StartMinimized { get; init; }

    /// <summary>"--elevated-relaunch": started by the elevation relaunch; wait for the old instance and never relaunch again.</summary>
    public bool ElevatedRelaunch { get; init; }

    /// <summary>"--quit": ask the running agent to exit cleanly (fans handed back) and exit; starts nothing.</summary>
    public bool Quit { get; init; }

    /// <summary>"--show-ui": open the popover once the pipe is up even with <see cref="StartMinimized"/> (elevation relaunch from an open popover).</summary>
    public bool ShowUi { get; init; }

    /// <summary>"--tab &lt;name&gt;": the popover tab to open with (devices / system / lighting / fans / settings).</summary>
    public string? Tab { get; init; }

    /// <summary>
    /// "--tray-selftest": developer smoke test of the Win32 tray pieces (icon, renderer, GDI handle counts, pipe label,
    /// device-change throttle). Touches no single-instance object, settings, state or hardware; exits by itself.
    /// </summary>
    public bool TraySelfTest { get; init; }

    /// <summary>Whether the popover should be opened once the agent is up.</summary>
    public bool OpenUiAtStart => ShowUi || !StartMinimized;

    public static AgentOptions Parse(IReadOnlyList<string> args)
    {
        var options = new AgentOptions();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case MinimizedArgument:
                case "-m":
                    options = options with { StartMinimized = true };
                    break;
                case "--elevated-relaunch":
                    options = options with { ElevatedRelaunch = true };
                    break;
                case "--quit":
                    options = options with { Quit = true };
                    break;
                case ShowUiArgument:
                    options = options with { ShowUi = true };
                    break;
                case "--tab":
                    if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    {
                        options = options with { Tab = args[++i].Trim().ToLowerInvariant() };
                    }
                    break;
                case "--tray-selftest":
                    options = options with { TraySelfTest = true };
                    break;
            }
        }

        return options;
    }
}
