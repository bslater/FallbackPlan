using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Store keys for the non-blob namespaces (specification 01 §2): snapshots,
/// index deltas and checkpoints, journal records. Identifiers render as
/// 26-character lowercase base32 (00 §6); generations and sequences render
/// as zero-padded 16-digit decimal so lexicographic order matches numeric
/// order.
/// </summary>
public static class MetadataStoreKeys
{
    /// <summary>The standalone snapshot object's key: <c>snapshots/&lt;device&gt;/&lt;set&gt;/&lt;snapshot&gt;</c> (specification 06 §6).</summary>
    public static ObjectKey Snapshot(ReadOnlySpan<byte> deviceId, ReadOnlySpan<byte> backupSetId, ReadOnlySpan<byte> snapshotId)
    {
        Require16(deviceId, nameof(deviceId));
        Require16(backupSetId, nameof(backupSetId));
        Require16(snapshotId, nameof(snapshotId));

        return ObjectKey.Parse(
            $"snapshots/{Base32.Encode(deviceId.ToArray())}/{Base32.Encode(backupSetId.ToArray())}/{Base32.Encode(snapshotId.ToArray())}");
    }

    /// <summary>An index delta's key: <c>index/delta/&lt;generation&gt;/&lt;delta-id&gt;</c> (specification 07 §2).</summary>
    public static ObjectKey IndexDelta(ulong generation, DeltaId deltaId) =>
        ObjectKey.Parse($"index/delta/{Decimal16(generation)}/{deltaId.ToBase32()}");

    /// <summary>An index checkpoint's key: <c>index/checkpoint/&lt;generation&gt;/&lt;checkpoint-id&gt;</c> (specification 07 §5).</summary>
    public static ObjectKey IndexCheckpoint(ulong generation, CheckpointId checkpointId) =>
        ObjectKey.Parse($"index/checkpoint/{Decimal16(generation)}/{checkpointId.ToBase32()}");

    /// <summary>A journal record's key: <c>journal/&lt;writer-id&gt;/&lt;sequence&gt;</c> (specification 08 §1).</summary>
    public static ObjectKey Journal(WriterId writerId, ulong sequence) =>
        ObjectKey.Parse($"journal/{Base32.Encode(writerId.ToArray())}/{Decimal16(sequence)}");

    /// <summary>The zero-padded 16-digit decimal rendering (specification 01 §2).</summary>
    public static string Decimal16(ulong value) => value.ToString("D16", CultureInfo.InvariantCulture);

    private static void Require16(ReadOnlySpan<byte> value, string name)
    {
        if (value.Length != 16)
        {
            throw new ArgumentException("The identifier is exactly 16 bytes.", name);
        }
    }
}
