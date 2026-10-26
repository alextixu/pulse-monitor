using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Rgb;
using Xunit;

namespace Pulse.Core.Tests.Rgb;

/// <summary>Pins the on-disk behaviour of the RGB defaults file (src/Pulse.Rgb/RgbDefaultsStore.cs) in a throw-away directory.</summary>
public sealed class RgbDefaultsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public RgbDefaultsStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "rgb-defaults.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private RgbDefaultsStore NewStore() => new(_path, NullLogger.Instance);

    private static RgbDeviceDefault Snapshot(string key, string deviceName = "Device", int modeIndex = 0)
        => new()
        {
            Key = key,
            DeviceName = deviceName,
            CapturedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ModeIndex = modeIndex,
            ModeName = "Static",
            ModeColors = new List<string> { "#FF0000" },
            LedColors = new List<string> { "#00FF00", "#0000FF" },
        };

    [Fact]
    public void MissingFile_IsAnEmptyStore()
    {
        var store = NewStore();
        Assert.False(store.Contains("x"));
        Assert.Null(store.Get("x"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AddMissing_AddsOnlyUnknownKeys_NeverOverwrites_AndReturnsTheCountAdded()
    {
        var store = NewStore();
        store.Set(Snapshot("k1", deviceName: "first"));

        var added = store.AddMissing(new[] { Snapshot("k1", deviceName: "second"), Snapshot("k2"), Snapshot("k2", deviceName: "dup") });

        Assert.Equal(1, added);
        Assert.Equal("first", store.Get("k1")!.DeviceName);
        Assert.Equal("Device", store.Get("k2")!.DeviceName);
        Assert.Equal(0, store.AddMissing(new[] { Snapshot("k1"), Snapshot("k2") }));
    }

    [Fact]
    public void AddMissing_WithNothingNew_DoesNotCreateTheFile()
    {
        var store = NewStore();
        Assert.Equal(0, store.AddMissing(Array.Empty<RgbDeviceDefault>()));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Set_ReplacesThePreviousSnapshotForTheSameKey()
    {
        var store = NewStore();
        store.Set(Snapshot("k", deviceName: "old", modeIndex: 1));

        store.Set(Snapshot("k", deviceName: "new", modeIndex: 3));

        var got = store.Get("k")!;
        Assert.Equal(("new", 3), (got.DeviceName, got.ModeIndex));
    }

    [Fact]
    public void Saved_SnapshotsAreReadBackByANewStoreInstance_IncludingColourStrings()
    {
        NewStore().Set(Snapshot("k", deviceName: "Keyboard", modeIndex: 2));

        var got = NewStore().Get("k");

        Assert.NotNull(got);
        Assert.Equal("Keyboard", got.DeviceName);
        Assert.Equal(2, got.ModeIndex);
        Assert.Equal("Static", got.ModeName);
        Assert.Equal(new[] { "#FF0000" }, got.ModeColors);
        Assert.Equal(new[] { "#00FF00", "#0000FF" }, got.LedColors);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), got.CapturedAt);
    }

    [Fact]
    public void CorruptFile_YieldsAnEmptyStore_KeepsACorruptCopy_AndIsRewrittenOnTheNextSave()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = NewStore();
        Assert.False(store.Contains("anything"));
        Assert.Equal("{ this is not json", File.ReadAllText(_path + ".corrupt"));

        store.Set(Snapshot("k"));

        Assert.True(NewStore().Contains("k"));
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileIsLeftBehind_AndDevicesAreSortedByKey()
    {
        var store = NewStore();
        store.AddMissing(new[] { Snapshot("zeta"), Snapshot("alpha"), Snapshot("mid") });

        Assert.Equal(new[] { "rgb-defaults.json" }, Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)));
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var keys = doc.RootElement.GetProperty("Devices").EnumerateArray().Select(d => d.GetProperty("Key").GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, keys);
    }

    [Fact]
    public void Load_SkipsEntriesWithoutAKey()
    {
        File.WriteAllText(_path, """{ "Version": 1, "Devices": [ { "Key": "", "DeviceName": "x" }, { "Key": "ok", "DeviceName": "y" } ] }""");

        var store = NewStore();

        Assert.True(store.Contains("ok"));
        Assert.False(store.Contains(""));
    }

    [Fact]
    public void BlankPath_FallsBackToTheDefaultPathUnderAppData()
    {
        var store = new RgbDefaultsStore("   ", NullLogger.Instance);
        Assert.Equal(RgbDefaultsStore.DefaultPath, store.FilePath);
        Assert.EndsWith(Path.Combine("Pulse", "rgb-defaults.json"), RgbDefaultsStore.DefaultPath);
    }
}
