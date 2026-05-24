using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Thin wrappers around the storage CIM provider under
/// <c>root\Microsoft\Windows\Storage</c>. Same classes that back the
/// PowerShell Storage module (<c>Update-Disk</c>, <c>Get-Partition</c>,
/// <c>Resize-Partition</c>).
/// </summary>
/// <remarks>
/// Why WSM and not VDS: cloudbase-init's VDS path hits a documented Windows
/// disk-mgmt rounding bug (<c>VDS_E_EXTENT_SIZE_LESS_THAN_MIN</c>) that the
/// WSM path side-steps because <c>MSFT_Partition.GetSupportedSize</c> already
/// returns a size the platform promises to accept. WSM is available on every
/// supported Windows Server release we care about (2012+).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CimStorage
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    // Partition Type values we refuse to extend automatically. These are the
    // localised strings returned by MSFT_Partition.Type. Recovery partitions
    // sitting between the OS partition and unallocated end-of-disk would
    // otherwise eat the new free space, leaving the OS partition stuck —
    // this is the exact scenario operators most often hit.
    private static readonly HashSet<string> ProtectedPartitionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",     // EFI System
        "Reserved",   // Microsoft Reserved
        "Recovery",   // Windows Recovery Environment
    };

    /// <summary>
    /// Calls <c>MSFT_Disk.Update</c> on every online, writable disk so the
    /// guest picks up new geometry after the host enlarged the VHD — without
    /// this, the GPT secondary header is still at the old end-of-disk and
    /// <c>GetSupportedSize</c> returns the pre-resize value.
    /// </summary>
    public static void UpdateDisks(ILogger logger)
    {
        using var session = CimSession.Create(null);
        foreach (var disk in session.EnumerateInstances(StorageNamespace, "MSFT_Disk"))
        {
            using (disk)
            {
                var number = GetUInt32(disk, "Number");
                var isReadOnly = GetBoolean(disk, "IsReadOnly") ?? false;
                var operationalStatus = GetUInt16Array(disk, "OperationalStatus");
                // OperationalStatus 1 = Other, 2 = OK, 3 = Degraded, 4 = Stressed,
                // ..., 53 = Online, 54 = Not Ready, 57 = Offline. We Update() only
                // when at least one entry is "OK" or "Online".
                var isOnline = operationalStatus.Length == 0
                               || Array.Exists(operationalStatus, s => s is 2 or 53);

                if (isReadOnly || !isOnline)
                {
                    logger.LogDebug(
                        "Skipping disk {Number} (read-only={ReadOnly}, online={Online})",
                        number, isReadOnly, isOnline);
                    continue;
                }

                try
                {
                    using var result = session.InvokeMethod(disk, "Update", new CimMethodParametersCollection());
                    var rc = (uint)(result.ReturnValue.Value ?? 0u);
                    if (rc != 0)
                    {
                        // Non-fatal: Update() can return non-zero on disks that
                        // simply have nothing to refresh. We log and continue;
                        // the resize step still runs and either succeeds or
                        // returns SizeMax == Size, which is a safe no-op.
                        logger.LogWarning(
                            "MSFT_Disk.Update() returned {Rc} for disk {Number}; continuing.",
                            rc, number);
                    }
                }
                catch (CimException ex)
                {
                    logger.LogWarning(ex,
                        "MSFT_Disk.Update() threw for disk {Number}; continuing.",
                        number);
                }
            }
        }
    }

    /// <summary>
    /// Enumerate every partition; if it qualifies for growth and its drive
    /// letter matches the (optional) filter, resize it to the platform's
    /// reported maximum supported size. Returns one result per
    /// resize-eligible partition (extended or not) so callers can log.
    /// </summary>
    public static IReadOnlyList<VolumeExtendResult> ExtendPartitions(
        IReadOnlySet<char>? driveLetterFilter,
        ILogger logger)
    {
        var results = new List<VolumeExtendResult>();
        using var session = CimSession.Create(null);

        foreach (var partition in session.EnumerateInstances(StorageNamespace, "MSFT_Partition"))
        {
            using (partition)
            {
                var size = GetUInt64(partition, "Size");
                if (size == 0)
                    continue;

                var isReadOnly = GetBoolean(partition, "IsReadOnly") ?? false;
                if (isReadOnly)
                    continue;

                var type = GetString(partition, "Type") ?? string.Empty;
                var driveLetter = GetDriveLetter(partition);

                // When the operator did NOT specify a drive-letter filter
                // (i.e. "grow everything that can grow"), we still refuse to
                // extend system / reserved / recovery partitions — letting
                // those swallow the unallocated tail of the disk is the
                // exact mistake we are guarding the operator from.
                if (driveLetterFilter is null)
                {
                    if (ProtectedPartitionTypes.Contains(type))
                    {
                        logger.LogDebug(
                            "Skipping protected partition type '{Type}' on disk {Disk} #{Number}.",
                            type, GetUInt32(partition, "DiskNumber"), GetUInt32(partition, "PartitionNumber"));
                        continue;
                    }
                }
                else
                {
                    // Drive letter filter is set — partitions without a
                    // letter are simply not addressable by the operator's
                    // selector, so they are out of scope here.
                    if (driveLetter is null)
                        continue;

                    if (!driveLetterFilter.Contains(driveLetter.Value))
                        continue;
                }

                // GetSupportedSize returns (rc, SizeMin, SizeMax). On
                // partitions that cannot grow (e.g. middle-of-disk, fixed by
                // a sibling partition) SizeMax equals Size and we no-op.
                ulong sizeMax;
                try
                {
                    sizeMax = QueryMaxSupportedSize(session, partition);
                }
                // QueryMaxSupportedSize can throw two distinct shapes: CimException
                // for transport / CIM-level failures, and InvalidOperationException
                // when MSFT_Partition.GetSupportedSize returns a non-zero RC (e.g.
                // partition is read-only, MSR/recovery, or the disk is offline).
                // Both must skip the partition rather than aborting the entire run.
                catch (Exception ex) when (ex is CimException or InvalidOperationException)
                {
                    logger.LogWarning(ex,
                        "GetSupportedSize failed for partition #{Number} on disk {Disk}; skipping.",
                        GetUInt32(partition, "PartitionNumber"), GetUInt32(partition, "DiskNumber"));
                    continue;
                }

                var volumeId = GetVolumeIdFor(session, partition) ?? string.Empty;
                if (sizeMax <= size)
                {
                    // Surface the no-op result too — operators want to see
                    // "volume considered but already at max".
                    results.Add(new VolumeExtendResult
                    {
                        DriveLetter = driveLetter,
                        VolumeId = volumeId,
                        SizeBefore = size,
                        SizeAfter = size,
                    });
                    continue;
                }

                logger.LogInformation(
                    "Extending partition #{Number} on disk {Disk} ({Letter}) from {From} to {To} bytes.",
                    GetUInt32(partition, "PartitionNumber"),
                    GetUInt32(partition, "DiskNumber"),
                    driveLetter?.ToString() ?? "no-letter",
                    size, sizeMax);

                try
                {
                    using var parameters = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("Size", sizeMax, CimType.UInt64, CimFlags.None),
                    };
                    using var result = session.InvokeMethod(partition, "Resize", parameters);
                    var rc = (uint)(result.ReturnValue.Value ?? 0u);
                    if (rc != 0)
                    {
                        logger.LogWarning(
                            "MSFT_Partition.Resize returned {Rc} for partition #{Number} on disk {Disk}; reported max was {Max}.",
                            rc,
                            GetUInt32(partition, "PartitionNumber"),
                            GetUInt32(partition, "DiskNumber"),
                            sizeMax);
                        results.Add(new VolumeExtendResult
                        {
                            DriveLetter = driveLetter,
                            VolumeId = volumeId,
                            SizeBefore = size,
                            SizeAfter = size,
                        });
                        continue;
                    }
                }
                catch (CimException ex)
                {
                    logger.LogWarning(ex,
                        "MSFT_Partition.Resize threw for partition #{Number} on disk {Disk}; skipping.",
                        GetUInt32(partition, "PartitionNumber"), GetUInt32(partition, "DiskNumber"));
                    continue;
                }

                results.Add(new VolumeExtendResult
                {
                    DriveLetter = driveLetter,
                    VolumeId = volumeId,
                    SizeBefore = size,
                    SizeAfter = sizeMax,
                });
            }
        }

        return results;
    }

    private static ulong QueryMaxSupportedSize(CimSession session, CimInstance partition)
    {
        using var parameters = new CimMethodParametersCollection();
        using var result = session.InvokeMethod(partition, "GetSupportedSize", parameters);

        var rc = (uint)(result.ReturnValue.Value ?? 0u);
        if (rc != 0)
            throw new InvalidOperationException(
                $"MSFT_Partition.GetSupportedSize returned {rc}.");

        var sizeMaxRaw = result.OutParameters["SizeMax"]?.Value;
        return sizeMaxRaw switch
        {
            ulong u => u,
            long l => (ulong)l,
            uint i => i,
            _ => 0ul,
        };
    }

    private static string? GetVolumeIdFor(CimSession session, CimInstance partition)
    {
        // MSFT_Partition exposes the volume association via
        // MSFT_PartitionToVolume. We don't always need the volume id (the
        // partition path is the resize target), but we surface it for log
        // correlation when no drive letter is mounted.
        try
        {
            foreach (var v in session.EnumerateAssociatedInstances(
                StorageNamespace, partition, "MSFT_PartitionToVolume", "MSFT_Volume",
                sourceRole: null, resultRole: null))
            {
                using (v)
                    return GetString(v, "UniqueId") ?? GetString(v, "ObjectId");
            }
        }
        catch (CimException)
        {
            // Some volumes (e.g. raw/EFI on certain images) refuse the
            // association walk. Best-effort.
        }
        return null;
    }

    private static char? GetDriveLetter(CimInstance partition)
    {
        // MSFT_Partition.DriveLetter is exposed as Char16 — surfaced by the
        // MI client as a ushort (the wide-char code) or as a string of length 1.
        var raw = partition.CimInstanceProperties["DriveLetter"]?.Value;
        char ch = raw switch
        {
            char c => c,
            ushort u => (char)u,
            string s when s.Length > 0 => s[0],
            _ => '\0',
        };
        if (ch is >= 'A' and <= 'Z') return ch;
        if (ch is >= 'a' and <= 'z') return char.ToUpperInvariant(ch);
        return null;
    }

    private static uint GetUInt32(CimInstance instance, string property)
    {
        var value = instance.CimInstanceProperties[property]?.Value;
        return value switch
        {
            uint u => u,
            int i => (uint)i,
            ushort s => s,
            _ => 0u,
        };
    }

    private static ulong GetUInt64(CimInstance instance, string property)
    {
        var value = instance.CimInstanceProperties[property]?.Value;
        return value switch
        {
            ulong u => u,
            long l => (ulong)l,
            uint i => i,
            _ => 0ul,
        };
    }

    private static ushort[] GetUInt16Array(CimInstance instance, string property)
    {
        var value = instance.CimInstanceProperties[property]?.Value;
        return value switch
        {
            ushort[] u => u,
            ushort u => [u],
            _ => [],
        };
    }

    private static string? GetString(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as string;

    private static bool? GetBoolean(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as bool?;
}
