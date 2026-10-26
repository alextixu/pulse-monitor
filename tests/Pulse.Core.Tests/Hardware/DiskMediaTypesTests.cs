using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace Pulse.Core.Tests.Hardware;

public sealed class DiskMediaTypesTests(ITestOutputHelper output)
{
    [Fact]
    public void Read_queries_the_storage_descriptors_without_elevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var disks = DiskMediaTypes.Read(NullLogger.Instance);

        Assert.NotNull(disks);
        foreach (var d in disks!) output.WriteLine($"{d.Model} bus={d.Bus ?? "?"} hdd={d.IsHardDisk}");
    }

    [Theory]
    [InlineData("CT2000T500SSD5", "NVMe CT2000T500SSD5")]
    [InlineData("ADATA SX8200PNP", "ADATA SX 8200PNP")]
    [InlineData("ST2000LX001-1RG174", "ST2000LX001-1RG174")]
    public void Find_matches_LHM_names_to_descriptor_models(string lhmName, string model)
    {
        var disks = new[] { new DiskMetadata(model, false, null), new DiskMetadata("Other Disk 9000", true, null) };

        Assert.Same(disks[0], DiskMediaTypes.Find(lhmName, disks));
    }
}
