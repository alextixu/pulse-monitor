using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pulse.App.ViewModels;
using Pulse.Core.Models;

namespace Pulse.App.Services;

/// <summary>
/// What the popover remembers between its (short) lives: the theme, so the window opens in the right colours before the
/// agent answered, and the last tab. %LOCALAPPDATA%\Pulse\ui-state.json; the agent's settings stay the source of truth.
/// </summary>
public sealed record UiState
{
    public AppTheme Theme { get; init; } = AppTheme.System;
    public MainTab? LastTab { get; init; }
}

[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UiState))]
internal sealed partial class UiStateJsonContext : JsonSerializerContext
{
}

public static class UiStateStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "ui-state.json");

    /// <summary>The cached state, or defaults when the file is missing or unreadable (never throws).</summary>
    public static UiState Load(ILogger log)
    {
        try
        {
            if (!File.Exists(DefaultPath)) return new UiState();
            var state = JsonSerializer.Deserialize(File.ReadAllText(DefaultPath), UiStateJsonContext.Default.UiState) ?? new UiState();
            return state with
            {
                Theme = Enum.IsDefined(state.Theme) ? state.Theme : AppTheme.System,
                LastTab = state.LastTab is { } tab && Enum.IsDefined(tab) ? tab : null,
            };
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "ui-state.json unreadable; using defaults");
            return new UiState();
        }
    }

    /// <summary>Writes the state (temp file + move). Never throws: losing it only costs the theme of the next first frame.</summary>
    public static void Save(UiState state, ILogger log)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultPath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $"ui-state.{Environment.ProcessId}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(state, UiStateJsonContext.Default.UiState));
            File.Move(temp, DefaultPath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Writing ui-state.json failed");
        }
    }
}
