namespace Pulse.App;

/// <summary>Parsed command line. Immutable; created once in <see cref="Program"/> and exposed as <see cref="App.Options"/>.</summary>
public sealed record AppOptions
{
    /// <summary>"--minimized": start with the popover hidden (tray only).</summary>
    public bool StartMinimized { get; init; }

    /// <summary>"--elevated-relaunch": this instance was started by <c>IElevationService.RelaunchElevated</c>; never relaunch again.</summary>
    public bool ElevatedRelaunch { get; init; }

    /// <summary>"--demo": fake providers with design data, no hardware access.</summary>
    public bool Demo { get; init; }

    /// <summary>"--screenshot &lt;dir&gt;": render every tab to PNG files in the directory and exit (implies demo).</summary>
    public string? ScreenshotDirectory { get; init; }

    public bool IsScreenshotMode => ScreenshotDirectory is not null;

    /// <summary>"--quit": ask the running instance to exit cleanly (restoring fans) and exit; starts no UI.</summary>
    public bool Quit { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized":
                case "-m":
                    options = options with { StartMinimized = true };
                    break;
                case "--elevated-relaunch":
                    options = options with { ElevatedRelaunch = true };
                    break;
                case "--demo":
                    options = options with { Demo = true };
                    break;
                case "--quit":
                    options = options with { Quit = true };
                    break;
                case "--screenshot":
                    if (i + 1 < args.Length)
                    {
                        options = options with { Demo = true, StartMinimized = true, ScreenshotDirectory = Path.GetFullPath(args[++i]) };
                    }
                    break;
            }
        }

        return options;
    }
}
