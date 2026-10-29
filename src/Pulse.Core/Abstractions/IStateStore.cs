using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>
/// The agent's memory (記憶) that is not a user setting: last battery readings, remembered lighting, fan first sights of
/// this boot, low-battery alerts already shown. %LOCALAPPDATA%\Pulse\state.json in the agent; in memory in demo mode.
/// </summary>
public interface IStateStore
{
    string FilePath { get; }

    PulseState Load();

    /// <summary>Persists the state (callers debounce). Never throws.</summary>
    void Save(PulseState state);
}

/// <summary>Keeps the state in memory only (demo / screenshot runs never touch the user's files).</summary>
public sealed class InMemoryStateStore : IStateStore
{
    private PulseState _state = new();

    public string FilePath => "(memory)";

    public PulseState Load() => _state;

    public void Save(PulseState state) => _state = state;
}
