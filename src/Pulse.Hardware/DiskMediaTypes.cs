using System.Management;
using Microsoft.Extensions.Logging;

namespace Pulse.Hardware;

/// <summary>Windows' view of one physical disk (no commands are sent to the drive to obtain it).</summary>
internal sealed record DiskMetadata(string Model, bool IsHardDisk, string? Bus);

/// <summary>
/// Disk models, media types and bus types from Windows' storage metadata (MSFT_PhysicalDisk). Reading this metadata does
/// not send commands to the drives, so it is safe for sleeping hard disks.
/// </summary>
internal static class DiskMediaTypes
{
    private const ushort MediaTypeHdd = 3;

    public static IReadOnlyList<DiskMetadata> Read(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiskMetadata>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            var disks = new List<DiskMetadata>();
            foreach (var disk in searcher.Get())
            {
                using (disk)
                {
                    if (disk["FriendlyName"] is not string name || name.Trim().Length == 0) continue;
                    var hdd = disk["MediaType"] is ushort type && type == MediaTypeHdd;
                    disks.Add(new DiskMetadata(name.Trim(), hdd, BusName(disk["BusType"])));
                }
            }
            logger.LogInformation("Disks: {Disks}", disks.Count == 0 ? "(none)"
                : string.Join(", ", disks.Select(d => $"{d.Model} [{d.Bus ?? "?"}{(d.IsHardDisk ? ", HDD: not polled" : "")}]")));
            return disks;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read disk metadata; all drives will be polled");
            return Array.Empty<DiskMetadata>();
        }
    }

    /// <summary>
    /// The metadata entry whose model matches an LHM drive name: exact first, then ignoring spaces / hyphens / underscores
    /// (Windows reports "ADATA SX 8200PNP" where LHM says "ADATA SX8200PNP"), then containment of the normalised forms.
    /// </summary>
    public static DiskMetadata? Find(string driveName, IReadOnlyList<DiskMetadata> disks)
    {
        var name = driveName.Trim();
        var key = Normalize(name);
        return disks.FirstOrDefault(d => d.Model.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? disks.FirstOrDefault(d => Normalize(d.Model) == key)
               ?? disks.FirstOrDefault(d => key.Length > 0 && (key.Contains(Normalize(d.Model), StringComparison.Ordinal) || Normalize(d.Model).Contains(key, StringComparison.Ordinal)));
    }

    private static string Normalize(string model)
        => new(model.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').Select(char.ToUpperInvariant).ToArray());

    private static string? BusName(object? busType) => busType is ushort bus ? bus switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        3 => "ATA",
        _ => null,
    } : null;
}
