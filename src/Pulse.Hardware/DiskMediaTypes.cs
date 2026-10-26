using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Hardware;

/// <summary>Windows' view of one physical disk (no commands are sent to the drive to obtain it).</summary>
internal sealed record DiskMetadata(string Model, bool IsHardDisk, string? Bus);

/// <summary>
/// Disk models, bus types and whether a disk rotates, from the storage stack's cached descriptors
/// (IOCTL_STORAGE_QUERY_PROPERTY on \\.\PhysicalDriveN opened without read or write access). The port driver answers from
/// data it already holds, so this is safe for sleeping hard disks, takes about a millisecond and needs neither WMI nor
/// elevation.
/// </summary>
internal static partial class DiskMediaTypes
{
    private const int MaxDrives = 32;

    /// <summary>The disks, or null when nothing could be read — callers must then poll no drive at all.</summary>
    public static IReadOnlyList<DiskMetadata>? Read(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiskMetadata>();
        try
        {
            var disks = new List<DiskMetadata>();
            for (var i = 0; i < MaxDrives; i++)
            {
                using var handle = CreateFileW($@"\\.\PhysicalDrive{i}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                if (QueryDevice(handle) is not { } device) continue;
                // Same rule as Windows' MSFT_PhysicalDisk (used before): only a drive that reports a seek penalty is a
                // hard disk; USB bridges that cannot answer stay pollable, as they were.
                var hdd = QuerySeekPenalty(handle) ?? false;
                disks.Add(device with { IsHardDisk = hdd });
            }
            logger.LogInformation("Disks: {Disks}", disks.Count == 0 ? "(none)"
                : string.Join(", ", disks.Select(d => $"{d.Model} [{d.Bus ?? "?"}{(d.IsHardDisk ? ", HDD: not polled" : "")}]")));
            return disks;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read disk metadata; no drive will be polled");
            return null;
        }
    }

    /// <summary>
    /// The metadata entry whose model matches an LHM drive name: exact first, then ignoring spaces / hyphens / underscores
    /// ("ADATA SX 8200PNP" vs "ADATA SX8200PNP"), then containment of the normalised forms (the descriptor may prefix the
    /// product id with a vendor such as "NVMe").
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

    private static unsafe DiskMetadata? QueryDevice(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceProperty, QueryType = PropertyStandardQuery };
        var buffer = new byte[1024];
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), p, (uint)buffer.Length, out var returned, IntPtr.Zero)
                || returned < 32)
            {
                return null;
            }

            var vendor = ReadAnsi(buffer, BitConverter.ToInt32(buffer, 12), (int)returned);
            var product = ReadAnsi(buffer, BitConverter.ToInt32(buffer, 16), (int)returned);
            var bus = BitConverter.ToInt32(buffer, 28);
            var model = string.Join(' ', new[] { vendor, product }.Where(s => s.Length > 0));
            return model.Length == 0 ? null : new DiskMetadata(model, false, BusName(bus));
        }
    }

    private static unsafe bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
        var buffer = stackalloc byte[12];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), buffer, 12, out var returned, IntPtr.Zero)
            || returned < 9)
        {
            return null;
        }
        return buffer[8] != 0;
    }

    private static string ReadAnsi(byte[] buffer, int offset, int length)
    {
        if (offset <= 0 || offset >= length) return string.Empty;
        var end = offset;
        while (end < length && buffer[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    private static string? BusName(int busType) => busType switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        3 => "ATA",
        _ => null,
    };

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
