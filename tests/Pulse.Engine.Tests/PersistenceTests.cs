using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Engine.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pulse-engine-tests", Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ReadsASettingsFileWrittenByTheOldApp()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "HardwareRefreshSeconds": 2,
              "BatteryRefreshSeconds": 30,
              "OpenRgbHost": "127.0.0.1",
              "OpenRgbPort": 6742,
              "StartWithSystem": true,
              "Theme": "Dark",
              "TrayIconMode": "CpuTemperature",
              "LowBatteryThresholdPercent": 15,
              "HiddenDeviceIds": [ "bt:x", "bt:x", "" ],
              "FanMode": "Quiet",
              "SyncedFanPercent": 45,
              "FanGroupIncludedIds": [ "pump" ],
              // comments and trailing commas are accepted
              "TelemetryEnabled": false,
            }
            """);
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, path);

        var settings = store.Load();

        Assert.True(settings.StartWithSystem);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(TrayIconMode.CpuTemperature, settings.TrayIconMode);
        Assert.Equal(15, settings.LowBatteryThresholdPercent);
        Assert.Equal(new[] { "bt:x" }, settings.HiddenDeviceIds);
        Assert.Equal(FanMode.Quiet, settings.FanMode);
        Assert.Equal(45, settings.SyncedFanPercent);
        Assert.Equal(new[] { "pump" }, settings.FanGroupIncludedIds);
        Assert.False(settings.TelemetryEnabled);
        Assert.True(settings.RestoreLightingOnStartup); // new key, default

        store.Save(settings);
        var written = File.ReadAllText(path);
        Assert.Contains("\"Theme\": \"Dark\"", written);
        Assert.Contains("\"FanMode\": \"Quiet\"", written);
        Assert.Equal(AppTheme.Dark, store.Load().Theme);
    }

    [Fact]
    public void StateRoundTripsEveryKindOfMemory()
    {
        var path = Path.Combine(_dir, "state.json");
        var store = new JsonStateStore(path, NullLogger<JsonStateStore>.Instance);
        var at = new DateTimeOffset(2026, 10, 27, 14, 5, 0, TimeSpan.FromHours(8));
        var state = new PulseState
        {
            Batteries = new[]
            {
                new RememberedBattery
                {
                    Device = new BatteryDevice { Id = "bt:1", Name = "耳機", Source = "Bluetooth", Percent = 40, Kind = DeviceKind.Headset, UpdatedAt = at },
                    LastConnectedAt = at,
                    LastReportedAt = at,
                },
            },
            Lighting = new Dictionary<string, LightingIntent>
            {
                ["Strip|loc|"] = new() { Kind = LightingIntentKind.Zones, DeviceName = "Strip", Zones = new Dictionary<int, string> { [0] = "#000000", [2] = "#0000FF" }, UpdatedAt = at },
                ["Mouse|hid|"] = new() { Kind = LightingIntentKind.Mode, ModeName = "Breathing", Color = "#FF0000", Speed = 50, UpdatedAt = at },
            },
            FanBootTime = at,
            Fans = new[] { new FanMemory("pump", 100, 3900, true) },
            LowBatteryNotified = new[] { "bt:1" },
        };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal("耳機", Assert.Single(loaded.Batteries).Device.Name);
        Assert.Equal(at, loaded.Batteries[0].LastConnectedAt);
        Assert.Equal(LightingIntentKind.Zones, loaded.Lighting["Strip|loc|"].Kind);
        Assert.Equal("#0000FF", loaded.Lighting["Strip|loc|"].Zones![2]);
        Assert.Equal("Breathing", loaded.Lighting["Mouse|hid|"].ModeName);
        Assert.Equal(at, loaded.FanBootTime);
        Assert.Equal(new FanMemory("pump", 100, 3900, true), Assert.Single(loaded.Fans));
        Assert.Equal(new[] { "bt:1" }, loaded.LowBatteryNotified);
        Assert.Contains("\"Kind\": \"Zones\"", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ACorruptStateFileIsKeptAsideAndAnEmptyStateUsed()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{ not json");
        var store = new JsonStateStore(path, NullLogger<JsonStateStore>.Instance);

        var state = store.Load();

        Assert.Empty(state.Batteries);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AMissingStateFileIsNotCreatedByLoading()
    {
        var path = Path.Combine(_dir, "sub", "state.json");
        var state = new JsonStateStore(path, NullLogger<JsonStateStore>.Instance).Load();
        Assert.Empty(state.Lighting);
        Assert.False(Directory.Exists(Path.Combine(_dir, "sub")));
    }
}
