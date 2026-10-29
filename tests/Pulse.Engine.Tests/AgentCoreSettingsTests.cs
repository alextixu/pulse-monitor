using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests;

public class AgentCoreSettingsTests
{
    [Fact]
    public async Task APatchBumpsTheRevisionBroadcastsAndIsSavedDebounced()
    {
        var store = new RecordingSettingsStore();
        await using var agent = TestAgent.Create(settings: store);
        var broadcasts = new List<SettingsState>();
        agent.SettingsChanged += (_, s) => { lock (broadcasts) broadcasts.Add(s); };
        var revision = agent.Settings.Revision;

        var result = await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Dark));

        Assert.True(result.Ok);
        Assert.Equal(AppTheme.Dark, agent.Settings.Settings.Theme);
        Assert.Equal(revision + 1, agent.Settings.Revision);
        Assert.Equal(nameof(AppSettings.Theme), agent.Settings.Changed);
        Assert.True(await TestAgent.Eventually(() => { lock (broadcasts) return broadcasts.Count == 1; }));
        Assert.True(await TestAgent.Eventually(() => store.Saves.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(AppTheme.Dark, store.Saves[0].Theme);

        // The same value again changes nothing: no revision, no broadcast, no save.
        Assert.True((await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Dark))).Ok);
        Assert.Equal(revision + 1, agent.Settings.Revision);
    }

    [Fact]
    public async Task TheSettingsSnapshotIsACopy()
    {
        await using var agent = TestAgent.Create();
        var snapshot = agent.Settings.Settings;
        snapshot.HiddenDeviceIds.Add("tampered");
        await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.LowBatteryThresholdPercent), 30));
        Assert.DoesNotContain("tampered", agent.Settings.Settings.HiddenDeviceIds);
    }

    [Theory]
    [InlineData(nameof(AppSettings.FanMode), "\"Quiet\"")]
    [InlineData(nameof(AppSettings.SyncedFanPercent), "70")]
    [InlineData(nameof(AppSettings.FanGroupExcludedIds), "[\"fan\"]")]
    [InlineData(nameof(AppSettings.FanGroupIncludedIds), "[\"fan\"]")]
    public async Task FanFieldsCannotBePatched(string name, string json)
    {
        await using var agent = TestAgent.Create();
        var revision = agent.Settings.Revision;
        var result = await agent.UpdateSettingAsync(new SettingsPatch(name, json));
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal(revision, agent.Settings.Revision);
    }

    [Fact]
    public async Task AnInvalidValueIsRejected()
    {
        await using var agent = TestAgent.Create();
        var result = await agent.UpdateSettingAsync(new SettingsPatch(nameof(AppSettings.BatteryRefreshSeconds), "\"soon\""));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task FanCommandsAreTheOnlyWritersOfTheFanFields()
    {
        await using var agent = TestAgent.Create();
        var r0 = agent.Settings.Revision;

        Assert.True((await agent.SetFanModeAsync(FanMode.Quiet)).Ok);
        Assert.Equal(FanMode.Quiet, agent.Settings.Settings.FanMode);
        Assert.Equal(r0 + 1, agent.Settings.Revision);
        Assert.True((await agent.SetFanModeAsync(FanMode.Quiet)).Ok);
        Assert.Equal(r0 + 1, agent.Settings.Revision);

        Assert.True((await agent.SetSyncedPercentAsync(33.4)).Ok);
        Assert.Equal(33, agent.Settings.Settings.SyncedFanPercent);
        Assert.True((await agent.SetSyncedPercentAsync(33.2)).Ok);
        Assert.Equal(r0 + 2, agent.Settings.Revision);
        Assert.True((await agent.SetSyncedPercentAsync(140)).Ok);
        Assert.Equal(100, agent.Settings.Settings.SyncedFanPercent);
        Assert.False((await agent.SetSyncedPercentAsync(double.NaN)).Ok);

        Assert.True((await agent.SetFanMembershipAsync("pump", include: true)).Ok);
        Assert.Contains("pump", agent.Settings.Settings.FanGroupIncludedIds);
        Assert.DoesNotContain("pump", agent.Settings.Settings.FanGroupExcludedIds);
        Assert.True((await agent.SetFanMembershipAsync("pump", include: false)).Ok);
        Assert.Contains("pump", agent.Settings.Settings.FanGroupExcludedIds);
        Assert.DoesNotContain("pump", agent.Settings.Settings.FanGroupIncludedIds);
        var revision = agent.Settings.Revision;
        Assert.True((await agent.SetFanMembershipAsync("pump", include: false)).Ok);
        Assert.Equal(revision, agent.Settings.Revision);
        Assert.False((await agent.SetFanMembershipAsync(" ", include: true)).Ok);
    }

    [Fact]
    public async Task ADemoEngineNeverSaves()
    {
        var store = new RecordingSettingsStore { FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json") };
        var state = new RecordingStateStore();
        var agent = TestAgent.Create(settings: store, state: state, demo: true);
        await agent.StartAsync();
        await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Light));
        await agent.SetFanModeAsync(FanMode.Auto);
        await Task.Delay(800);
        await agent.ShutdownAsync(AgentExitReason.Quit);
        Assert.Empty(store.Saves);
        Assert.Empty(state.Saves);
    }

    [Fact]
    public async Task FirstRunWritesTheDefaults()
    {
        var store = new RecordingSettingsStore { FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json") };
        var agent = TestAgent.Create(settings: store);
        await agent.StartAsync();
        Assert.True(await TestAgent.Eventually(() => store.Saves.Count >= 1));
        await agent.ShutdownAsync(AgentExitReason.Quit);
    }

    [Fact]
    public async Task HidingADeviceRepublishesTheBatteriesAndTheSummary()
    {
        var provider = new FakeBatteryProvider
        {
            Devices = () => new[]
            {
                new BatteryDevice { Id = "mouse", Name = "Mouse", Source = "Fake", Percent = 40 },
                new BatteryDevice { Id = "kb", Name = "Keyboard", Source = "Fake", Percent = 70 },
            },
        };
        await using var agent = TestAgent.Create(providers: new[] { provider });
        await agent.StartAsync();
        Assert.True(await TestAgent.Eventually(() => agent.Batteries.All.Count == 2));
        Assert.Equal("mouse", agent.Batteries.Lowest!.DeviceId);
        Assert.Equal("mouse", agent.Summary.Lowest!.DeviceId);

        var updates = 0;
        agent.BatteriesUpdated += (_, _) => Interlocked.Increment(ref updates);
        Assert.True((await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.HiddenDeviceIds), new[] { "mouse" }))).Ok);

        Assert.Equal(2, agent.Batteries.All.Count);
        Assert.Equal("kb", Assert.Single(agent.Batteries.Visible).Id);
        Assert.Equal("kb", agent.Batteries.Lowest!.DeviceId);
        Assert.Equal("kb", agent.Summary.Lowest!.DeviceId);
        Assert.True(updates >= 1);
    }

    [Fact]
    public async Task LowBatteryWhileHiddenBecomesANotificationAndAPendingToast()
    {
        var provider = new FakeBatteryProvider
        {
            Devices = () => new[] { new BatteryDevice { Id = "mouse", Name = "Mouse", Source = "Fake", Percent = 9, Status = BatteryStatus.Discharging } },
        };
        var state = new RecordingStateStore();
        await using var agent = TestAgent.Create(providers: new[] { provider }, state: state);
        var notifications = 0;
        var inPanel = 0;
        agent.NotificationRequested += (_, _) => Interlocked.Increment(ref notifications);
        agent.LowBattery += (_, _) => Interlocked.Increment(ref inPanel);

        await agent.StartAsync();
        Assert.True(await TestAgent.Eventually(() => Volatile.Read(ref notifications) == 1));
        Assert.Equal(0, inPanel);
        var toast = Assert.Single(agent.TakePendingToasts());
        Assert.Equal("mouse", toast.Device.Id);
        Assert.Empty(agent.TakePendingToasts());

        // Alerted once only, and remembered for the next start.
        await agent.RefreshNowAsync();
        Assert.Equal(1, notifications);
        await agent.ShutdownAsync(AgentExitReason.Quit);
        Assert.Contains("mouse", state.Saves[^1].LowBatteryNotified);
        Assert.Equal("mouse", Assert.Single(state.Saves[^1].Batteries).Device.Id);
    }

    [Fact]
    public async Task LowBatteryWhileVisibleGoesToThePanel()
    {
        var provider = new FakeBatteryProvider
        {
            Devices = () => new[] { new BatteryDevice { Id = "mouse", Name = "Mouse", Source = "Fake", Percent = 9 } },
        };
        await using var agent = TestAgent.Create(providers: new[] { provider });
        var inPanel = 0;
        agent.LowBattery += (_, _) => Interlocked.Increment(ref inPanel);
        agent.SetUiVisible(true);
        await agent.StartAsync();
        Assert.True(await TestAgent.Eventually(() => Volatile.Read(ref inPanel) == 1));
        Assert.Empty(agent.TakePendingToasts());
    }

    [Fact]
    public async Task RememberedBatteriesArePublishedBeforeTheFirstPoll()
    {
        var state = new RecordingStateStore
        {
            Initial = new PulseState
            {
                Batteries = new[]
                {
                    new RememberedBattery
                    {
                        Device = new BatteryDevice { Id = "kb", Name = "Keyboard", Source = "Fake", Percent = 61 },
                        LastConnectedAt = DateTimeOffset.Now.AddHours(-1),
                        LastReportedAt = DateTimeOffset.Now.AddHours(-1),
                    },
                },
            },
        };
        await using var agent = TestAgent.Create(state: state);
        var device = Assert.Single(agent.Batteries.All);
        Assert.False(device.IsConnected);
        Assert.Equal(61, device.Percent);
        Assert.StartsWith("上次 ", device.Detail);
    }
}
