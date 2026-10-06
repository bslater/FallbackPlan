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

    /// <summary>
    /// A source-identity pack's key:
    /// <c>hints/identity-pack/&lt;device&gt;/&lt;captured-at&gt;/&lt;snapshot&gt;/&lt;part&gt;</c>
    /// (specification 06 §11.5).
    /// </summary>
    /// <remarks>
    /// The <b>device</b> comes first because a source key is derived from
    /// one, so a reader lists only the packs that could answer it. The
    /// <b>capture time</b> follows as zero-padded decimal so that key order
    /// within a device is chronological, which lets a reader stop at the first
    /// pack past its bound. The <b>snapshot</b> separates two captures in one
    /// millisecond, and the <b>part</b> numbers a publication's packs when it
    /// created more versions than one holds. One child per publication rather
    /// than per file, so the prefix needs no shard.
    /// </remarks>
    public static ObjectKey SourceIdentityPack(
        ReadOnlySpan<byte> deviceId, ulong capturedAt, ReadOnlySpan<byte> snapshotId, uint part)
    {
        Require16(deviceId, nameof(deviceId));
        Require16(snapshotId, nameof(snapshotId));

        return ObjectKey.Parse(
            $"hints/identity-pack/{Base32.Encode(deviceId.ToArray())}/{Decimal16(capturedAt)}/{Base32.Encode(snapshotId.ToArray())}/{Decimal16(part)}");
    }

    /// <summary>Every source-identity pack one device published, in capture order.</summary>
    public static ObjectPrefix SourceIdentityPackPrefix(ReadOnlySpan<byte> deviceId)
    {
        Require16(deviceId, nameof(deviceId));
        return ObjectPrefix.Parse($"hints/identity-pack/{Base32.Encode(deviceId.ToArray())}/");
    }

    /// <summary>
    /// The prefix every per-file source-identity hint sits under, of every
    /// source key (specification 06 §11): what a reader asks once to learn
    /// whether any exist at all.
    /// </summary>
    public static ObjectPrefix SourceIdentityHintsPrefix { get; } = ObjectPrefix.Parse("hints/identity/");

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
