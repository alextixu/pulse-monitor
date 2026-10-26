using System.Text;
using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Ipc;
using Pulse.Core.Models;
using Pulse.Core.Tray;
using Xunit;

namespace Pulse.Core.Tests.Ipc;

public sealed class IpcCodecTests
{
    private static async Task<IpcMessage> RoundTrip(IpcMessage message)
    {
        using var stream = new MemoryStream();
        await IpcCodec.WriteAsync(stream, message);
        stream.Position = 0;
        var back = await IpcCodec.ReadAsync(stream);
        Assert.NotNull(back);
        Assert.Null(await IpcCodec.ReadAsync(stream)); // end of stream
        return back!;
    }

    private static HardwareFrame SampleFrame() => new(
        new HardwareSnapshot
        {
            Cpu = new CpuInfo { Name = "i7", LoadPercent = 12.5, PackageTempC = 61, CoreLoadsPercent = new double?[] { 1, null, 3 } },
            Gpus = new[] { new GpuInfo { Id = "/gpu-nvidia/0", Name = "RTX", Vendor = "NVIDIA", CoreTempC = double.NaN } },
            Fans = new[] { new FanInfo { Id = "/lpc/0/fan/0", Name = "Fan #1", Group = "主機板", Rpm = 1200, Percent = 35, CanControl = true } },
            Storages = new[] { new StorageInfo { Id = "/nvme/0", Name = "CT2000", TemperatureC = 45 } },
        },
        new FanModeResult
        {
            Mode = FanMode.Quiet,
            ControlAvailable = true,
            Fans = new Dictionary<string, FanModeFanStatus>
            {
                ["/lpc/0/fan/0"] = new("/lpc/0/fan/0", FanModeState.Quiet, 35, FanExclusion.None, false, 60, FanPresence.Visible),
            },
        });

    [Fact]
    public async Task Hello_round_trips_with_nested_records_dictionaries_and_NaN()
    {
        var battery = new BatteryDevice { Id = "bt:aa", Name = "Xbox", Source = "Bluetooth", Percent = 75, IsConnected = false, LastConnectedAt = DateTimeOffset.Now };
        var hello = new HelloMessage(
            IpcProtocol.Version,
            new AgentInfo { IsElevated = true, Version = "1.0", ProcessId = 42 },
            new SettingsState(new AppSettings { FanMode = FanMode.Quiet, HiddenDeviceIds = new() { "x" } }, 7, "FanMode"),
            new HardwareMonitorStatus { State = HardwareMonitorState.Ready, OrphanedFanIds = new[] { "/lpc/0/fan/0" } },
            new RgbStatus { State = RgbConnectionState.Connected, ServerVersion = "v4" },
            SampleFrame(),
            new BatteryState { All = new[] { battery }, Visible = new[] { battery }, Lowest = new BatterySummary("bt:aa", "Xbox", 75, false, DeviceKind.Controller) },
            new[] { new LowBatteryToast(battery, "低電量", "Xbox 剩 5%") });

        var back = Assert.IsType<HelloMessage>(await RoundTrip(hello));

        Assert.Equal(42, back.Info.ProcessId);
        Assert.Equal(FanMode.Quiet, back.Settings.Settings.FanMode);
        Assert.Equal(new[] { "x" }, back.Settings.Settings.HiddenDeviceIds);
        Assert.Equal(7, back.Settings.Revision);
        Assert.Equal(new[] { "/lpc/0/fan/0" }, back.HardwareStatus.OrphanedFanIds);
        Assert.True(double.IsNaN(back.Hardware.Snapshot.Gpus[0].CoreTempC!.Value));
        Assert.Equal(new double?[] { 1, null, 3 }, back.Hardware.Snapshot.Cpu!.CoreLoadsPercent);
        Assert.Equal(FanModeState.Quiet, back.Hardware.Fans.Fans["/lpc/0/fan/0"].State);
        Assert.Equal("主機板", back.Hardware.Snapshot.Fans[0].Group);
        Assert.Equal(75, back.Batteries.Lowest!.Percent);
        Assert.Single(back.PendingToasts);
    }

    [Fact]
    public async Task Rgb_devices_carry_colours_as_hex_strings()
    {
        var device = new RgbDevice
        {
            Index = 0, Name = "Kingston", Type = "DRAM", Key = "Kingston|I2C|", HasDefault = true,
            Colors = new[] { new RgbColor(255, 0, 16) },
            Modes = new[] { new RgbMode { Index = 0, Name = "Direct", IsPerLed = true } },
        };
        var reply = new RgbDevicesReplyMessage(3, true, null, new[] { device });

        var json = Encoding.UTF8.GetString(IpcCodec.Encode(reply), 4, IpcCodec.Encode(reply).Length - 4);
        Assert.Contains("\"#FF0010\"", json);

        var back = Assert.IsType<RgbDevicesReplyMessage>(await RoundTrip(reply));
        Assert.Equal(new RgbColor(255, 0, 16), back.Devices[0].Colors[0]);
        Assert.Equal("Kingston|I2C|", back.Devices[0].Key);
        Assert.True(back.Devices[0].HasDefault);
    }

    public static IEnumerable<object[]> Requests() => new[]
    {
        new object[] { new RefreshNowRequest(1) },
        new object[] { new ConnectRgbRequest(2) },
        new object[] { new RgbGetDevicesRequest(3, true) },
        new object[] { new RgbCommandRequest(4, new RgbCommand { Kind = RgbCommandKind.SetMode, Key = "k", ModeName = "Rainbow", Color = new RgbColor(1, 2, 3), Speed = 5 }) },
        new object[] { new SetFanRequest(5, "/lpc/0/fan/0", null) },
        new object[] { new SetFanModeRequest(6, FanMode.Synced) },
        new object[] { new SetSyncedPercentRequest(7, 42.5) },
        new object[] { new SetFanMembershipRequest(8, "f", true) },
        new object[] { new UpdateSettingRequest(9, SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Dark)) },
        new object[] { new SetAutoStartRequest(10, false) },
        new object[] { new ElevateRequest(11, true) },
        new object[] { new QuitRequest(12) },
        new object[] { new ClientHelloMessage(IpcProtocol.Version, 99) },
        new object[] { new SetVisibilityMessage(true) },
        new object[] { new ShowPopoverMessage(true, "Fans") },
        new object[] { new ExitingMessage(AgentExitReason.Relaunch) },
        new object[] { new ReplyMessage(5, false, "錯誤") },
        new object[] { new RgbDevicesChangedMessage() },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Every_message_type_round_trips_to_an_equal_record(IpcMessage message)
    {
        var back = await RoundTrip(message);

        Assert.Equal(message.GetType(), back.GetType());
        if (message is not RgbCommandRequest and not UpdateSettingRequest) Assert.Equal(message, back);
    }

    [Fact]
    public async Task Unknown_discriminator_from_a_newer_peer_becomes_a_plain_message()
    {
        var json = Encoding.UTF8.GetBytes("{\"t\":\"fromTheFuture\",\"x\":1}");
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(json.Length));
        stream.Write(json);
        stream.Position = 0;

        var back = await IpcCodec.ReadAsync(stream);

        Assert.Equal(typeof(IpcMessage), back!.GetType());
    }

    [Fact]
    public async Task Oversized_frame_length_is_rejected_before_allocating()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(IpcProtocol.MaxMessageBytes + 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcCodec.ReadAsync(stream));
    }

    [Fact]
    public void Settings_patch_changes_only_the_named_property_and_sanitises()
    {
        var settings = new AppSettings { FanMode = FanMode.Quiet, OpenRgbPort = 6742 };

        Assert.Null(SettingsPatch.Of(nameof(AppSettings.OpenRgbPort), 99999).ApplyTo(settings));
        Assert.Equal(65535, settings.OpenRgbPort);
        Assert.Equal(FanMode.Quiet, settings.FanMode);

        Assert.Null(SettingsPatch.Of(nameof(AppSettings.HiddenDeviceIds), new[] { "a", "a", " " }).ApplyTo(settings));
        Assert.Equal(new[] { "a" }, settings.HiddenDeviceIds);
    }

    [Fact]
    public void Settings_patch_refuses_fan_fields_and_bad_values()
    {
        var settings = new AppSettings();

        Assert.NotNull(new SettingsPatch(nameof(AppSettings.FanMode), "\"Synced\"").ApplyTo(settings));
        Assert.Equal(FanMode.Individual, settings.FanMode);
        Assert.NotNull(new SettingsPatch(nameof(AppSettings.OpenRgbPort), "\"abc\"").ApplyTo(settings));
    }

    [Fact]
    public void AppSettings_clone_is_deep()
    {
        var a = new AppSettings { HiddenDeviceIds = new() { "x" } };
        var b = a.Clone();
        b.HiddenDeviceIds.Add("y");

        Assert.Equal(new[] { "x" }, a.HiddenDeviceIds);
    }

    [Fact]
    public void Tooltip_keeps_whole_lines_and_ends_with_an_ellipsis_when_cut()
    {
        var devices = Enumerable.Range(0, 12).Select(i => new BatteryDevice { Id = $"d{i}", Name = $"Device number {i}", Source = "x", Percent = 50 }).ToList();
        var text = TrayContent.BuildTooltip(new TraySummary { ConnectedDevices = devices, CpuLoadPercent = 10, CpuTempC = 50 });

        Assert.True(text.Length <= TrayContent.MaxTooltipLength);
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("\r", text);
    }
}
