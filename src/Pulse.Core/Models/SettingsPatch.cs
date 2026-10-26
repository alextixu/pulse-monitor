using System.Text.Json;
using Pulse.Core.Ipc;

namespace Pulse.Core.Models;

/// <summary>
/// A change of one <see cref="AppSettings"/> property: its name and the new value as JSON. Sending single properties
/// instead of the whole object means a stale copy in the UI can never overwrite a field the agent changed meanwhile.
/// </summary>
public sealed record SettingsPatch(string Name, string ValueJson)
{
    /// <summary>The properties a client may change. Fan-mode fields are changed only through the fan commands.</summary>
    public static readonly IReadOnlySet<string> Patchable = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AppSettings.HardwareRefreshSeconds),
        nameof(AppSettings.BatteryRefreshSeconds),
        nameof(AppSettings.OpenRgbHost),
        nameof(AppSettings.OpenRgbPort),
        nameof(AppSettings.AutoConnectOpenRgb),
        nameof(AppSettings.UseBundledOpenRgb),
        nameof(AppSettings.StartWithSystem),
        nameof(AppSettings.RequestElevationOnStartup),
        nameof(AppSettings.Theme),
        nameof(AppSettings.TrayIconMode),
        nameof(AppSettings.NotifyOnLowBattery),
        nameof(AppSettings.LowBatteryThresholdPercent),
        nameof(AppSettings.HiddenDeviceIds),
        nameof(AppSettings.TelemetryEnabled),
        nameof(AppSettings.RestoreLightingOnStartup),
    };

    public static SettingsPatch Of(string name, int value) => new(name, JsonSerializer.Serialize(value, IpcJsonContext.Default.Int32));
    public static SettingsPatch Of(string name, bool value) => new(name, JsonSerializer.Serialize(value, IpcJsonContext.Default.Boolean));
    public static SettingsPatch Of(string name, string value) => new(name, JsonSerializer.Serialize(value, IpcJsonContext.Default.String));
    public static SettingsPatch Of(string name, AppTheme value) => new(name, JsonSerializer.Serialize(value, IpcJsonContext.Default.AppTheme));
    public static SettingsPatch Of(string name, TrayIconMode value) => new(name, JsonSerializer.Serialize(value, IpcJsonContext.Default.TrayIconMode));
    public static SettingsPatch Of(string name, IReadOnlyList<string> value) => new(name, JsonSerializer.Serialize(value.ToList(), IpcJsonContext.Default.ListString));

    /// <summary>Applies the patch to <paramref name="settings"/> (then sanitised). Returns an error text, or null on success.</summary>
    public string? ApplyTo(AppSettings settings)
    {
        if (!Patchable.Contains(Name)) return $"無法變更設定「{Name}」。";
        try
        {
            switch (Name)
            {
                case nameof(AppSettings.HardwareRefreshSeconds): settings.HardwareRefreshSeconds = Read(IpcJsonContext.Default.Int32); break;
                case nameof(AppSettings.BatteryRefreshSeconds): settings.BatteryRefreshSeconds = Read(IpcJsonContext.Default.Int32); break;
                case nameof(AppSettings.OpenRgbHost): settings.OpenRgbHost = Read(IpcJsonContext.Default.String) ?? string.Empty; break;
                case nameof(AppSettings.OpenRgbPort): settings.OpenRgbPort = Read(IpcJsonContext.Default.Int32); break;
                case nameof(AppSettings.AutoConnectOpenRgb): settings.AutoConnectOpenRgb = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.UseBundledOpenRgb): settings.UseBundledOpenRgb = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.StartWithSystem): settings.StartWithSystem = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.RequestElevationOnStartup): settings.RequestElevationOnStartup = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.Theme): settings.Theme = Read(IpcJsonContext.Default.AppTheme); break;
                case nameof(AppSettings.TrayIconMode): settings.TrayIconMode = Read(IpcJsonContext.Default.TrayIconMode); break;
                case nameof(AppSettings.NotifyOnLowBattery): settings.NotifyOnLowBattery = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.LowBatteryThresholdPercent): settings.LowBatteryThresholdPercent = Read(IpcJsonContext.Default.Int32); break;
                case nameof(AppSettings.HiddenDeviceIds): settings.HiddenDeviceIds = Read(IpcJsonContext.Default.ListString) ?? new List<string>(); break;
                case nameof(AppSettings.TelemetryEnabled): settings.TelemetryEnabled = Read(IpcJsonContext.Default.Boolean); break;
                case nameof(AppSettings.RestoreLightingOnStartup): settings.RestoreLightingOnStartup = Read(IpcJsonContext.Default.Boolean); break;
            }
            settings.Sanitize();
            return null;
        }
        catch (JsonException)
        {
            return $"設定「{Name}」的值無效。";
        }
    }

    private T Read<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.Deserialize(ValueJson, info)!;
}
