using Pulse.App.ViewModels;

namespace Pulse.App;

/// <summary>
/// Parsed command line of <c>Pulse.UI.exe [--popover] [--tab &lt;name&gt;] [--demo] [--screenshot &lt;dir&gt;]</c>.
/// Immutable; created once in <see cref="Program"/> and exposed as <see cref="App.Options"/>. Unknown arguments are ignored.
/// </summary>
public sealed record AppOptions
{
    /// <summary>"--popover": started by the agent's tray icon (informational; the popover is always shown at start).</summary>
    public bool Popover { get; init; }

    /// <summary>"--tab &lt;name&gt;": open on this tab (devices / system / lighting / fans / settings) instead of the last one.</summary>
    public MainTab? Tab { get; init; }

    /// <summary>"--demo": an in-process demo agent with design data, no pipe, no hardware access.</summary>
    public bool Demo { get; init; }

    /// <summary>"--screenshot &lt;dir&gt;": render every tab to PNG files in the directory and exit (implies demo).</summary>
    public string? ScreenshotDirectory { get; init; }

    public bool IsScreenshotMode => ScreenshotDirectory is not null;

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--popover":
                    options = options with { Popover = true };
                    break;
                case "--tab":
                    if (i + 1 < args.Length)
                    {
                        options = options with { Tab = ParseTab(args[++i]) };
                    }
                    break;
                case "--demo":
                    options = options with { Demo = true };
                    break;
                case "--screenshot":
                    if (i + 1 < args.Length)
                    {
                        options = options with { Demo = true, ScreenshotDirectory = Path.GetFullPath(args[++i]) };
                    }
                    break;
            }
        }

        return options;
    }

    /// <summary>"settings" / "Fans" … → the tab; null for anything else (also used for the agent's ShowPopover tab).</summary>
    public static MainTab? ParseTab(string? name)
        => !string.IsNullOrWhiteSpace(name) && Enum.TryParse<MainTab>(name.Trim(), ignoreCase: true, out var tab) && Enum.IsDefined(tab)
            ? tab
            : null;
}
