using Microsoft.Extensions.Logging;
using OpenRGB.NET;
using Pulse.Core.Models;
using Pulse.Rgb;
using Xunit;
using static Pulse.Core.Tests.Rgb.RgbDeviceIdentityTests;

namespace Pulse.Core.Tests.Rgb;

/// <summary>
/// Device identity through the real <see cref="OpenRgbController"/> against <see cref="FakeOpenRgbServer"/> (loopback,
/// ephemeral port): Key / HasDefault in the device list, migration of the defaults file on connect, the first-seen capture
/// guard and HasDefault after "save current as default". Defaults live in a throw-away directory.
/// </summary>
public sealed class OpenRgbControllerIdentityTests : IDisposable
{
    private static readonly Color Red = new(255, 59, 48);
    private static readonly Color Black = new(0, 0, 0);
    private const int DirectMode = 0, StaticMode = 1, OffMode = 2, RainbowMode = 3;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly ListLogger<OpenRgbController> _log = new();

    public OpenRgbControllerIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "rgb-defaults.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static FakeRgbDevice Falchion(string instance, Color color, int mode = DirectMode)
        => FakeRgbDevice.Create(FalchionName, 5, Location(FalchionA).Replace("8&1b8da785&0&0000", instance), 68, color, mode);

    private static FakeRgbDevice[] RealMachine(Color falchionColor) =>
    [
        Falchion("8&1ba891e3&0&0000", falchionColor),
        FakeRgbDevice.Create(GpuName, 2, Location(Gpu), 22, new Color(255, 255, 255), RainbowMode),
        FakeRgbDevice.Create(MotherboardName, 0, Location(Motherboard), 1, new Color(255, 255, 255), DirectMode, serial: "9876543210"),
        FakeRgbDevice.Create(DramName, 1, Location(Dram), 24, new Color(255, 214, 10), RainbowMode),
    ];

    private async Task<OpenRgbController> ConnectAsync(FakeOpenRgbServer server)
    {
        Assert.NotEqual(OpenRgbInfo.DefaultPort, server.Port); // never the user's real OpenRGB
        var rgb = new OpenRgbController(_log, _path);
        rgb.Configure("127.0.0.1", server.Port);
        Assert.True(await rgb.ConnectAsync(), _log.Dump());
        Assert.Equal(RgbConnectionState.Connected, rgb.Status.State);
        return rgb;
    }

    private async Task<IReadOnlyList<RgbDevice>> DevicesAsync(OpenRgbController rgb, bool refresh, int expected)
    {
        var devices = await rgb.GetDevicesAsync(refresh);
        Assert.True(devices.Count == expected, $"expected {expected} devices, got {devices.Count}{Environment.NewLine}{_log.Dump()}");
        return devices;
    }

    private RgbDefaultsStore Store() => new(_path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    [Fact]
    public async Task Connect_FirstSeenDevices_AreCapturedUnderTheNormalisedKey_AndReportKeyAndHasDefault()
    {
        using var server = new FakeOpenRgbServer(RealMachine(Red));
        using var rgb = await ConnectAsync(server);

        var devices = await DevicesAsync(rgb, refresh: false, expected: 4);

        Assert.Equal(new[] { FalchionKey, Gpu, MotherboardKey, Dram }, devices.Select(d => d.Key));
        Assert.All(devices, d => Assert.True(d.HasDefault, d.Key));
        Assert.True(rgb.HasDefault(0));

        var stored = Store().GetAll().ToDictionary(e => e.Key);
        Assert.Equal(new[] { Dram, Gpu, FalchionKey, MotherboardKey }.Order(StringComparer.Ordinal), stored.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(5, stored[FalchionKey].DeviceType);
        Assert.Equal(68, stored[FalchionKey].LedColors.Count);
        Assert.All(stored[FalchionKey].LedColors, c => Assert.Equal("#FF3B30", c));
    }

    [Fact]
    public async Task Connect_WithTheRealDefaultsFile_MigratesInsteadOfCapturing_AndDropsTheBlackDuplicate()
    {
        Store().Rewrite([], RealDefaults());
        using var server = new FakeOpenRgbServer(RealMachine(new Color(0, 120, 255))); // lit differently now
        using var rgb = await ConnectAsync(server);

        var devices = await DevicesAsync(rgb, refresh: false, expected: 4);

        Assert.All(devices, d => Assert.True(d.HasDefault, d.Key));
        var stored = Store().GetAll().ToDictionary(e => e.Key);
        Assert.Equal(new[] { Dram, Gpu, FalchionKey, MotherboardKey }.Order(StringComparer.Ordinal), stored.Keys.Order(StringComparer.Ordinal));

        // The Falchion keeps its original first-seen snapshot (red, 10/08), not today's colour and not the black one.
        var falchion = stored[FalchionKey];
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 4, 4, 8, TimeSpan.FromHours(8)), falchion.CapturedAt);
        Assert.All(falchion.LedColors, c => Assert.Equal("#FF3B30", c));
        Assert.Equal(5, falchion.DeviceType);
        Assert.Equal("Direct", stored[MotherboardKey].ModeName);

        Assert.Contains(_log.Entries, e => e.Message.Contains("moved to key") && e.Message.Contains(FalchionA));
        Assert.Contains(_log.Entries, e => e.Message.Contains("dropped") && e.Message.Contains(FalchionB));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("default captured"));
    }

    [Fact]
    public async Task CaptureGuard_OffModeOrAllBlack_IsNotCaptured_LoggedOnce_ThenCapturedOnceLit()
    {
        var offMode = FakeRgbDevice.Create(DramName, 1, Location(Dram), 24, new Color(255, 214, 10), OffMode);
        var black = Falchion("8&1ba891e3&0&0000", Black);
        using var server = new FakeOpenRgbServer(black, offMode);
        using var rgb = await ConnectAsync(server);

        var devices = await DevicesAsync(rgb, refresh: false, expected: 2);
        Assert.All(devices, d => Assert.False(d.HasDefault, d.Key));
        Assert.False(rgb.HasDefault(0));
        Assert.Empty(Store().GetAll());

        await DevicesAsync(rgb, refresh: true, expected: 2); // still off: no capture, no second log line
        Assert.Empty(Store().GetAll());
        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("looks switched off")));

        server.Mutate(d =>
        {
            d[0].Colors = Enumerable.Repeat(Red, 68).ToArray();
            d[1].ActiveMode = RainbowMode;
        });
        devices = await DevicesAsync(rgb, refresh: true, expected: 2);

        Assert.All(devices, d => Assert.True(d.HasDefault, d.Key));
        var stored = Store().GetAll().ToDictionary(e => e.Key);
        Assert.Equal("Rainbow", stored[Dram].ModeName);
        Assert.All(stored[FalchionKey].LedColors, c => Assert.Equal("#FF3B30", c));
    }

    [Fact]
    public async Task SaveCurrentAsDefault_TheCachedListReportsHasDefaultWithoutARefresh()
    {
        using var server = new FakeOpenRgbServer(Falchion("8&1ba891e3&0&0000", Black));
        using var rgb = await ConnectAsync(server);
        Assert.False((await DevicesAsync(rgb, refresh: false, expected: 1))[0].HasDefault);

        await rgb.SaveCurrentAsDefaultAsync(0);

        var device = (await DevicesAsync(rgb, refresh: false, expected: 1))[0];
        Assert.True(device.HasDefault);
        Assert.Equal(FalchionKey, device.Key);
        Assert.True(rgb.HasDefault(0));
        var saved = Assert.Single(Store().GetAll()); // an explicit save is not subject to the capture guard
        Assert.Equal(FalchionKey, saved.Key);
        Assert.All(saved.LedColors, c => Assert.Equal("#000000", c));
    }

    [Fact]
    public async Task ReadBacks_KeepKeyAndHasDefault_OnTheCachedDevice()
    {
        using var server = new FakeOpenRgbServer(RealMachine(Red));
        using var rgb = await ConnectAsync(server);
        await DevicesAsync(rgb, refresh: false, expected: 4);

        await rgb.SetDeviceColorAsync(1, new RgbColor(0, 255, 0)); // GPU: Rainbow → Direct (read-back) → UPDATELEDS
        await rgb.TurnOffAsync(2);                                  // motherboard: Off mode (read-back)

        var devices = await DevicesAsync(rgb, refresh: false, expected: 4);
        Assert.Equal(new[] { FalchionKey, Gpu, MotherboardKey, Dram }, devices.Select(d => d.Key));
        Assert.All(devices, d => Assert.True(d.HasDefault, d.Key));
        Assert.Equal(DirectMode, devices[1].ActiveModeIndex);
        Assert.Equal(OffMode, devices[2].ActiveModeIndex);
        Assert.Equal(RgbConnectionState.Connected, rgb.Status.State);
        Assert.Contains(1100u, server.Received); // SETCUSTOMMODE for the plain switch to Direct
        Assert.Contains(1101u, server.Received); // hand-built UPDATEMODE for Off
    }
}
