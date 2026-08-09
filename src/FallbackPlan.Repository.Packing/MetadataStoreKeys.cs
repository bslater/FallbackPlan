using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Packing.Resources;

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

    /// <summary>
    /// A source-identity hint's key:
    /// <c>hints/identity/&lt;shard&gt;/&lt;source-key&gt;/&lt;captured-at&gt;/&lt;snapshot&gt;</c>
    /// (specification 06 §11).
    /// </summary>
    /// <remarks>
    /// Three things the layout is doing. The <b>shard</b> keeps
    /// <c>hints/identity/</c> from holding one child per file in the
    /// repository, exactly as it does for blobs and for the reason 01 §2
    /// gives. The <b>capture time</b> is zero-padded decimal so that
    /// lexicographic key order within one source key is chronological, which
    /// is what lets a reader take the newest version at or before a bound
    /// without reading every object. The <b>snapshot identifier</b> is last
    /// so two snapshots captured in the same millisecond cannot collide.
    /// </remarks>
    public static ObjectKey SourceIdentityHint(
        ReadOnlySpan<byte> sourceKey, ulong capturedAt, ReadOnlySpan<byte> snapshotId)
    {
        Require16(snapshotId, nameof(snapshotId));

        var rendered = RenderSourceKey(sourceKey);
        return ObjectKey.Parse(
            $"hints/identity/{rendered[..ShardLength]}/{rendered}/{Decimal16(capturedAt)}/{Base32.Encode(snapshotId.ToArray())}");
    }

    /// <summary>Every hint published for one source file, in capture order.</summary>
    public static ObjectPrefix SourceIdentityPrefix(ReadOnlySpan<byte> sourceKey)
    {
        var rendered = RenderSourceKey(sourceKey);
        return ObjectPrefix.Parse($"hints/identity/{rendered[..ShardLength]}/{rendered}/");
    }

    /// <summary>The shard is the first four base32 characters, as it is for blobs (specification 01 §2).</summary>
    private const int ShardLength = 4;

    private static string RenderSourceKey(ReadOnlySpan<byte> sourceKey)
    {
        Require16(sourceKey, nameof(sourceKey));
        return Base32.Encode(sourceKey.ToArray());
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

    /// <summary>The width of a <see cref="Decimal16"/> rendering.</summary>
    public const int Decimal16Length = 16;

    private static void Require16(ReadOnlySpan<byte> value, string name)
    {
        if (value.Length != 16)
        {
            throw new ArgumentException(Strings.MetadataStoreKeys_IdentifierExactlyBytes, name);
        }
    }
}
