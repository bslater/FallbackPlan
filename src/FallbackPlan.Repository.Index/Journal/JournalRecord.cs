using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Index.Journal;

/// <summary>The journal record kinds (specification 08 §2 key 1).</summary>
public enum JournalRecordKind : ushort
{
    /// <summary>A write intent — the durable statement that blobs are in flight (08 §3).</summary>
    WriteIntent = 1,

    /// <summary>An intent extension naming additional blobs (08 §4).</summary>
    IntentExtension = 2,

    /// <summary>An intent retirement — an event, not the absence of a heartbeat (08 §5).</summary>
    IntentRetirement = 3,

    /// <summary>An audit record for a destructive operation (08 §6).</summary>
    Audit = 4,
}

/// <summary>Why a writer creates blobs (specification 08 §3 payload key 5).</summary>
public enum IntentPurpose : ushort
{
    /// <summary>An ordinary backup.</summary>
    Backup = 1,

    /// <summary>Compaction — collectors are writers too, with no exception for maintenance (08 §3.2).</summary>
    Compaction = 2,

    /// <summary>Healing.</summary>
    Healing = 3,

    /// <summary>Import.</summary>
    Import = 4,
}

/// <summary>How an intent ended (specification 08 §5 payload key 2).</summary>
public enum IntentOutcome : ushort
{
    /// <summary>The covered work became reachable.</summary>
    Completed = 1,

    /// <summary>The job was abandoned.</summary>
    Abandoned = 2,

    /// <summary>An operator force-expired it — audited, never automatic (08 §7.1).</summary>
    ForceExpired = 3,
}

/// <summary>The audited destructive actions (specification 08 §6 key 1; shape per ADR-0022 §Decision 6).</summary>
public enum AuditAction : ushort
{
    /// <summary>Retention reduction.</summary>
    RetentionReduction = 1,

    /// <summary>Bulk snapshot deletion.</summary>
    BulkSnapshotDeletion = 2,

    /// <summary>A garbage-collection pass.</summary>
    GcPass = 3,

    /// <summary>A force-expiry (08 §7.1).</summary>
    ForceExpiry = 4,
}

/// <summary>The per-kind payload (specification 08 §3–§6). Exactly one shape per kind.</summary>
public abstract record JournalPayload
{
    private JournalPayload()
    {
    }

    /// <summary>A write intent's payload (08 §3, keys 1–5).</summary>
    public sealed record WriteIntent(
        ReadOnlyMemory<byte> BackupSetId,
        IReadOnlyList<BlobId> IntendedBlobIds,
        ulong DeclaredMaxDurationMs,
        ulong ExpiryGeneration,
        IntentPurpose Purpose) : JournalPayload;

    /// <summary>An intent extension's payload (08 §4, keys 1–3).</summary>
    public sealed record IntentExtension(
        ulong ExtendsSequence,
        IReadOnlyList<BlobId> AdditionalBlobIds,
        ulong DeclaredMaxDurationMs) : JournalPayload;

    /// <summary>An intent retirement's payload (08 §5, keys 1–2).</summary>
    public sealed record IntentRetirement(
        ulong RetiresSequence,
        IntentOutcome Outcome) : JournalPayload;

    /// <summary>An audit record's payload (08 §6, keys 1–4); parameters are per-action and empty in phase 0.</summary>
    public sealed record Audit(
        AuditAction Action,
        string Actor,
        ulong ObjectsAffected) : JournalPayload;
}

/// <summary>One journal record (specification 08 §2, keys 1–5; the signature rides at key 6).</summary>
public sealed record JournalRecord(
    JournalRecordKind Kind,
    WriterId WriterId,
    ulong Sequence,
    ulong IssuedAt,
    JournalPayload Payload);

/// <summary>
/// A decoded journal record with its signed bytes and signature. The record
/// carries no generation field — a verifier tries the signing key of each
/// generation from the bundle's current value downward and accepts the first
/// that verifies (08 §2 key 6).
/// </summary>
public sealed record DecodedJournalRecord(
    JournalRecord Record,
    ReadOnlyMemory<byte> SignedBytes,
    ReadOnlyMemory<byte> Signature);
