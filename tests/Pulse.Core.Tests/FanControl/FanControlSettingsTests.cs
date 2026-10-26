using Pulse.Core.FanControl;
using Pulse.Core.Models;
using Xunit;

namespace Pulse.Core.Tests.FanControl;

/// <summary>Pins the settings snapshot handed from the UI to the controller (FanControlSettings in FanModeController.cs).</summary>
public sealed class FanControlSettingsTests
{
    [Fact]
    public void From_CopiesModeSyncedPercentAndAllThreeIdSets()
    {
        var app = new AppSettings
        {
            FanMode = FanMode.Synced,
            SyncedFanPercent = 63,
            FanGroupExcludedIds = new List<string> { "x1", "x2" },
            FanGroupIncludedIds = new List<string> { "i1" },
        };
        var status = new HardwareMonitorStatus { OrphanedFanIds = new[] { "o1", "o2" } };

        var s = FanControlSettings.From(app, status);

        Assert.Equal(FanMode.Synced, s.Mode);
        Assert.Equal(63, s.SyncedPercent);
        Assert.Equal(new[] { "x1", "x2" }, s.ExcludedIds.OrderBy(x => x));
        Assert.Equal(new[] { "i1" }, s.IncludedIds);
        Assert.Equal(new[] { "o1", "o2" }, s.OrphanedIds.OrderBy(x => x));
    }

    [Fact]
    public void From_IsADefensiveCopy_LaterChangesToTheAppSettingsDoNotLeakIn()
    {
        var app = new AppSettings { FanGroupExcludedIds = new List<string> { "x1" } };
        var status = new HardwareMonitorStatus { OrphanedFanIds = new List<string> { "o1" } };

        var s = FanControlSettings.From(app, status);
        app.FanGroupExcludedIds.Add("x2");
        ((List<string>)status.OrphanedFanIds).Add("o2");

        Assert.Equal(new[] { "x1" }, s.ExcludedIds);
        Assert.Equal(new[] { "o1" }, s.OrphanedIds);
    }

    [Fact]
    public void From_WithoutAStatusOrWithNullLists_YieldsEmptySets()
    {
        var app = new AppSettings { FanGroupExcludedIds = null!, FanGroupIncludedIds = null! };

        var s = FanControlSettings.From(app);

        Assert.Empty(s.ExcludedIds);
        Assert.Empty(s.IncludedIds);
        Assert.Empty(s.OrphanedIds);
        Assert.Equal(FanMode.Individual, s.Mode);
        Assert.Equal(50, s.SyncedPercent);
    }

    [Fact]
    public void IdSets_UseOrdinalComparison()
    {
        var app = new AppSettings { FanGroupExcludedIds = new List<string> { "Fan" } };

        var s = FanControlSettings.From(app);

        Assert.Contains("Fan", s.ExcludedIds);
        Assert.DoesNotContain("fan", s.ExcludedIds);
    }
}
