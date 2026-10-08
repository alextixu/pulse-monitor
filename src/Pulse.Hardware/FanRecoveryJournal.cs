using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>
/// Records which fans this process has put in manual mode, so the next start can tell whether the previous session
/// ended without handing them back (crash, forced kill). Written on every change, deleted on a clean shutdown.
/// </summary>
internal sealed class FanRecoveryJournal
{
    public sealed record Entry(string Id, string Name, double Percent);

    private sealed record Document(int ProcessId, DateTimeOffset UpdatedAt, List<Entry> Fans);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;

    public FanRecoveryJournal(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "fan-recovery.json");

    /// <summary>Fans left in manual mode by a previous session, or an empty list.</summary>
    public IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<Entry>();
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
            return document?.Fans ?? (IReadOnlyList<Entry>)Array.Empty<Entry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan recovery journal {Path} unreadable; ignoring it", _path);
            return Array.Empty<Entry>();
        }
    }

    /// <summary>Replaces the journal with the current manual fans; deletes it when none are manual.</summary>
    public void Write(IReadOnlyCollection<Entry> manualFans)
    {
        if (manualFans.Count == 0)
        {
            Delete();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            var document = new Document(Environment.ProcessId, DateTimeOffset.Now, manualFans.ToList());
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
