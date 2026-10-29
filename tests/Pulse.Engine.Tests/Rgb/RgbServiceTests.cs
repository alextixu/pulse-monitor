using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine.Memory;
using Pulse.Engine.Rgb;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests.Rgb;

public class RgbServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Virtual time: every delay completes at once and moves the clock.</summary>
    private sealed class VirtualTime
    {
        public DateTimeOffset Now = T0;
        public DateTimeOffset Clock() => Now;

        public Task Delay(TimeSpan by, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += by;
            return Task.CompletedTask;
        }
    }

    private static Dictionary<string, LightingIntent> Intents() => new()
    {
        ["A|loc|"] = new() { Kind = LightingIntentKind.Off },
        ["B|loc|"] = new() { Kind = LightingIntentKind.Color, Color = "#FF0000" },
        ["C|late|"] = new() { Kind = LightingIntentKind.Mode, ModeName = "breathing", Color = "#00FF00", Speed = 50, Brightness = 80 },
        ["D|zones|"] = new() { Kind = LightingIntentKind.Zones, Zones = new Dictionary<int, string> { [0] = "#0000FF", [5] = "#FFFFFF" } },
    };

    private static (RgbService Service, FakeRgbController Rgb, VirtualTime Time, AppSettings Settings) Create(
        Dictionary<string, LightingIntent> intents, FakeLauncher? launcher = null)
    {
        var rgb = new FakeRgbController();
        rgb.Devices.Add(FakeRgbController.Device(0, "A|loc|"));
        rgb.Devices.Add(FakeRgbController.Device(1, "B|loc|"));
        rgb.Devices.Add(FakeRgbController.Device(2, "D|zones|", zones: 2));
        var time = new VirtualTime();
        var settings = new AppSettings { UseBundledOpenRgb = launcher is not null, RestoreLightingOnStartup = true };
        var service = new RgbService(rgb, launcher ?? new FakeLauncher(), new LightingMemory(intents), () => settings,
            NullLogger.Instance, time.Clock, time.Delay);
        return (service, rgb, time, settings);
    }

    [Fact]
    public async Task RestoresEveryRememberedDeviceOnceIncludingLateOnes()
    {
        var (service, rgb, _, _) = Create(Intents());
        var changes = 0;
        service.DevicesChanged += () => Interlocked.Increment(ref changes);
        // "C" shows up at the third re-read (≈ 30 s after the connect).
        rgb.OnRefresh = n =>
        {
            if (n == 3) lock (rgb.Devices) rgb.Devices.Add(FakeRgbController.Device(3, "C|late|"));
        };

        Assert.True(await service.ConnectAsync());
        await service.RestoreTask!;

        Assert.Equal(new[]
        {
            "TurnOff:0",
            "SetDeviceColor:1:#FF0000",
            "SetZoneColor:2:0:#0000FF", // zone 5 does not exist on this device: skipped
            "SetMode:3:2:#00FF00:50:",  // Breathing takes colour and speed, not brightness
        }, rgb.Calls);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task NeverRestoresAgainAfterAReconnect()
    {
        var (service, rgb, _, _) = Create(Intents());
        Assert.True(await service.ConnectAsync());
        await service.RestoreTask!;
        var calls = rgb.Calls.Count;
        var firstRestore = service.RestoreTask;

        // The server drops; the service reconnects on its own (virtual back-off).
        rgb.SetStatus(rgb.Status with { State = RgbConnectionState.Error });
        Assert.True(await TestAgent.Eventually(() => rgb.Connects >= 2));
        Assert.True(await service.ConnectAsync());

        Assert.Same(firstRestore, service.RestoreTask);
        Assert.Equal(calls, rgb.Calls.Count);
    }

    [Fact]
    public async Task DevicesThatAppearAfterTheWindowAreNotRestored()
    {
        var (service, rgb, time, _) = Create(new Dictionary<string, LightingIntent> { ["C|late|"] = new() { Kind = LightingIntentKind.Off } });
        rgb.OnRefresh = _ =>
        {
            if (time.Now - T0 > RgbService.RestoreWindow) lock (rgb.Devices) rgb.Devices.Add(FakeRgbController.Device(3, "C|late|"));
        };

        Assert.True(await service.ConnectAsync());
        await service.RestoreTask!;
        Assert.Empty(rgb.Calls);
        Assert.True(time.Now - T0 >= RgbService.RestoreWindow);
    }

    [Fact]
    public async Task NoRestoreWhenTheSettingIsOff()
    {
        var (service, rgb, _, settings) = Create(Intents());
        settings.RestoreLightingOnStartup = false;
        Assert.True(await service.ConnectAsync());
        Assert.Null(service.RestoreTask);
        Assert.Empty(rgb.Calls);
    }

    [Fact]
    public async Task CommandsResolveTheKeyAndRecordTheIntent()
    {
        var (service, rgb, _, settings) = Create(new Dictionary<string, LightingIntent>());
        settings.RestoreLightingOnStartup = false;
        await service.ConnectAsync();
        var memoryChanges = 0;
        service.MemoryChanged += () => memoryChanges++;

        var result = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.SetColor, Key = "B|loc|", Index = 0, Color = RgbColor.FromHex("#00FF00") });
        Assert.True(result.Ok);
        Assert.Equal(3, result.Devices.Count);
        Assert.Equal("SetDeviceColor:1:#00FF00", Assert.Single(rgb.Calls)); // the index hint (0) is ignored
        Assert.Equal(1, memoryChanges);
        Assert.Equal("#00FF00", service.ExportMemory()["B|loc|"].Color);

        var gone = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.TurnOff, Key = "Z|gone|" });
        Assert.False(gone.Ok);
        Assert.Equal(EngineStrings.RgbDeviceChanged, gone.Error);

        var all = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.TurnOff });
        Assert.True(all.Ok);
        Assert.Equal("TurnOffAll", rgb.Calls[^1]);
        Assert.All(service.ExportMemory().Values, i => Assert.Equal(LightingIntentKind.Off, i.Kind));

        var mode = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.SetMode, Key = "A|loc|", ModeName = "Static", ModeIndex = 3 });
        Assert.True(mode.Ok);
        Assert.Equal("SetMode:0:1:::", rgb.Calls[^1]); // by name, not by the stale index

        var forget = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.Forget, Key = "A|loc|" });
        Assert.True(forget.Ok);
        Assert.False(service.ExportMemory().ContainsKey("A|loc|"));
    }

    [Fact]
    public async Task AKeyMissingFromTheCacheIsLookedUpAfterARefresh()
    {
        var (service, rgb, _, settings) = Create(new Dictionary<string, LightingIntent>());
        settings.RestoreLightingOnStartup = false;
        await service.ConnectAsync();
        rgb.OnRefresh = n => { if (n == 1) lock (rgb.Devices) rgb.Devices.Add(FakeRgbController.Device(7, "New|usb|")); };

        var result = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.TurnOff, Key = "New|usb|" });
        Assert.True(result.Ok);
        Assert.Equal("TurnOff:7", Assert.Single(rgb.Calls));
    }

    [Fact]
    public async Task CommandsFailWhileDisconnected()
    {
        var (service, _, _, _) = Create(new Dictionary<string, LightingIntent>());
        var result = await service.ExecuteAsync(new RgbCommand { Kind = RgbCommandKind.TurnOff, Key = "A|loc|" });
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task AServerThatWasAlreadyRunningIsNeverLaunchedLater()
    {
        var launcher = new FakeLauncher { Result = RgbLaunchResult.AlreadyRunning };
        var (service, _, _, _) = Create(new Dictionary<string, LightingIntent>(), launcher);
        await service.ConnectAsync();
        await service.ConnectAsync();
        Assert.Equal(1, launcher.Launches);

        var started = new FakeLauncher { Result = RgbLaunchResult.Started };
        var (relaunching, _, _, _) = Create(new Dictionary<string, LightingIntent>(), started);
        await relaunching.ConnectAsync();
        await relaunching.ConnectAsync();
        Assert.Equal(2, started.Launches);
    }
}
