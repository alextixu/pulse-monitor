using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Engine;

/// <summary>state.json in %LOCALAPPDATA%\Pulse: atomic writes (temp file + move); a corrupt file is kept as .corrupt and replaced by an empty state.</summary>
public sealed class JsonStateStore : IStateStore
{
    public JsonStateStore(string filePath, ILogger<JsonStateStore> log) => throw new NotImplementedException();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "state.json");

    public string FilePath => throw new NotImplementedException();

    public PulseState Load() => throw new NotImplementedException();

    public void Save(PulseState state) => throw new NotImplementedException();
}
