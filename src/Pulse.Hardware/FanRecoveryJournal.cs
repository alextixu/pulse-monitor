using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>
/// Records which fans this process has put in manual mode, so the next start can tell whether the previous session
/// ended without handing them back (crash, forced kill). Written on every change, deleted on a clean shutdown.
/// It also remembers "orphaned" fans (firmware curve lost until reboot) together with the boot time, so a Pulse restart
/// before the reboot still knows not to hand them back; after a reboot that list is discarded.
/// </summary>
internal sealed class FanRecoveryJournal
{
    public sealed record Entry(string Id, string Name, double Percent);

    public sealed record State(IReadOnlyList<Entry> ManualFans, IReadOnlyList<string> OrphanedFanIds);

    private sealed record Document(int ProcessId, DateTimeOffset UpdatedAt, List<Entry> Fans, DateTimeOffset? BootTime = null, List<string>? Orphaned = null);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Boot times computed from the tick count drift a little; anything closer than this is the same boot.</summary>
    private static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    private readonly string _path;
    private readonly ILogger _logger;

    public FanRecoveryJournal(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "fan-recovery.json");

    public static DateTimeOffset CurrentBootTime => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Fans left in manual mode by a previous session, plus the orphaned fans recorded during this boot.</summary>
    public State Load()
    {
        try
        {
            if (!File.Exists(_path)) return new State(Array.Empty<Entry>(), Array.Empty<string>());
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
            if (document is null) return new State(Array.Empty<Entry>(), Array.Empty<string>());

            var sameBoot = document.BootTime is { } boot && (boot - CurrentBootTime).Duration() < SameBootTolerance;
            IReadOnlyList<string> orphaned = sameBoot ? document.Orphaned ?? new List<string>() : Array.Empty<string>();
            // After a reboot the firmware owns every fan again: earlier manual entries no longer matter either.
            IReadOnlyList<Entry> fans = sameBoot || document.BootTime is null ? document.Fans ?? new List<Entry>() : Array.Empty<Entry>();
            return new State(fans, orphaned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan recovery journal {Path} unreadable; ignoring it", _path);
            return new State(Array.Empty<Entry>(), Array.Empty<string>());
        }
    }

    /// <summary>Replaces the journal; deletes it when nothing is manual and nothing is orphaned.</summary>
    public void Write(IReadOnlyCollection<Entry> manualFans, IReadOnlyCollection<string> orphanedFanIds)
    {
        if (manualFans.Count == 0 && orphanedFanIds.Count == 0)
        {
            Delete();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            var document = new Document(Environment.ProcessId, DateTimeOffset.Now, manualFans.ToList(), CurrentBootTime, orphanedFanIds.ToList());
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write fan recovery journal {Path}", _path);
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete fan recovery journal {Path}", _path);
        }
    }
}
