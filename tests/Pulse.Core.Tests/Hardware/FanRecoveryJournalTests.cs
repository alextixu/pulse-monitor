using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Hardware;
using Xunit;

namespace Pulse.Core.Tests.Hardware;

/// <summary>Pins the crash-recovery journal (src/Pulse.Hardware/FanRecoveryJournal.cs) in a throw-away directory.</summary>
public sealed class FanRecoveryJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PulseTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly FanRecoveryJournal _journal;

    public FanRecoveryJournalTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "fan-recovery.json");
        _journal = new FanRecoveryJournal(_path, NullLogger.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly FanRecoveryJournal.Entry FanA = new("a", "Fan A", 40);
    private static readonly FanRecoveryJournal.Entry FanB = new("b", "Fan B", 100);

    /// <summary>Writes a document the way the journal does (PascalCase, default System.Text.Json), with or without a BootTime.</summary>
    private void WriteDocument(DateTimeOffset? bootTime, bool includeBootTime = true)
    {
        var fans = new[] { new { Id = "a", Name = "Fan A", Percent = 40.0 }, new { Id = "b", Name = "Fan B", Percent = 100.0 } };
        var orphaned = new[] { "o1", "o2" };
        object document = includeBootTime
            ? new { ProcessId = 1234, UpdatedAt = DateTimeOffset.Now, Fans = fans, BootTime = bootTime, Orphaned = orphaned }
            : new { ProcessId = 1234, UpdatedAt = DateTimeOffset.Now, Fans = fans, Orphaned = orphaned };
        File.WriteAllText(_path, JsonSerializer.Serialize(document));
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var state = _journal.Load();
        Assert.Empty(state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Fact]
    public void Load_BootTimeMoreThanTwoMinutesOff_DropsBothOrphansAndManualFans()
    {
        WriteDocument(FanRecoveryJournal.CurrentBootTime - TimeSpan.FromMinutes(2.5));

        var state = _journal.Load();

        Assert.Empty(state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Fact]
    public void Load_BootTimeFromAFutureBoot_IsAlsoADifferentBoot()
    {
        WriteDocument(FanRecoveryJournal.CurrentBootTime + TimeSpan.FromMinutes(3));

        var state = _journal.Load();

        Assert.Empty(state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    public void Load_BootTimeWithinTheTwoMinuteTolerance_KeepsOrphansAndManualFans(double driftSeconds)
    {
        WriteDocument(FanRecoveryJournal.CurrentBootTime + TimeSpan.FromSeconds(driftSeconds));

        var state = _journal.Load();

        Assert.Equal(new[] { FanA, FanB }, state.ManualFans);
        Assert.Equal(new[] { "o1", "o2" }, state.OrphanedFanIds);
    }

    [Fact]
    public void Load_LegacyDocumentWithoutBootTime_KeepsManualFans_ButNoOrphans()
    {
        WriteDocument(null, includeBootTime: false);

        var state = _journal.Load();

        Assert.Equal(new[] { FanA, FanB }, state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Fact]
    public void Load_ExplicitNullBootTime_BehavesLikeTheLegacyDocument()
    {
        WriteDocument(null, includeBootTime: true);

        var state = _journal.Load();

        Assert.Equal(new[] { FanA, FanB }, state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Fact]
    public void Load_CorruptFile_IsEmpty()
    {
        File.WriteAllText(_path, "{ definitely not json");

        var state = _journal.Load();

        Assert.Empty(state.ManualFans);
        Assert.Empty(state.OrphanedFanIds);
    }

    [Fact]
    public void Write_ThenLoad_RoundTripsBothLists_WithinTheSameBoot()
    {
        _journal.Write(new[] { FanA, FanB }, new[] { "o1" });

        Assert.True(File.Exists(_path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var state = _journal.Load();
        Assert.Equal(new[] { FanA, FanB }, state.ManualFans);
        Assert.Equal(new[] { "o1" }, state.OrphanedFanIds);

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("ProcessId").GetInt32());
        var boot = doc.RootElement.GetProperty("BootTime").GetDateTimeOffset();
        Assert.True((boot - FanRecoveryJournal.CurrentBootTime).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Write_OrphansOnly_StillKeepsTheFile()
    {
        _journal.Write(Array.Empty<FanRecoveryJournal.Entry>(), new[] { "o1" });

        Assert.True(File.Exists(_path));
        var state = _journal.Load();
        Assert.Empty(state.ManualFans);
        Assert.Equal(new[] { "o1" }, state.OrphanedFanIds);
    }

    [Fact]
    public void Write_WithBothListsEmpty_DeletesTheFile()
    {
        _journal.Write(new[] { FanA }, new[] { "o1" });
        Assert.True(File.Exists(_path));

        _journal.Write(Array.Empty<FanRecoveryJournal.Entry>(), Array.Empty<string>());

        Assert.False(File.Exists(_path));
        Assert.Empty(_journal.Load().ManualFans);
    }

    [Fact]
    public void Delete_OnAMissingFile_IsHarmless()
    {
        _journal.Delete();
        Assert.False(File.Exists(_path));
    }
}
