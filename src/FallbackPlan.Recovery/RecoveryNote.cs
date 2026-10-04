using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Recovery;

/// <summary>What a recovery note is about.</summary>
public enum RecoveryNoteKind
{
    /// <summary>A blob whose recovery footer did not open, so the whole blob was skipped.</summary>
    BlobSkipped = 0,

    /// <summary>A tree entry whose name is not a plain component, refused with its subtree.</summary>
    Refused = 1,

    /// <summary>An entry of a kind only the full client materialises.</summary>
    SkippedSpecial = 2,

    /// <summary>A file's segment that is in no readable blob.</summary>
    SegmentMissing = 3,

    /// <summary>A file's segment whose record did not read.</summary>
    SegmentUnreadable = 4,

    /// <summary>A file whose whole-file hash did not verify.</summary>
    HashMismatch = 5,

    /// <summary>A metadata record that is in no readable blob.</summary>
    RecordMissing = 6,

    /// <summary>A metadata record that did not read.</summary>
    RecordUnreadable = 7,
}

/// <summary>
/// One thing a recovery run could not do, held as fields rather than as a
/// sentence (ADR-0082). The operator's terminal reads <see cref="ToString"/>,
/// which says what the tool always said; the diagnostic bundle renders each
/// field by its declared type, so a path is a path and a reader's words are
/// text no type cleared.
/// </summary>
/// <param name="Kind">What the note is about.</param>
/// <param name="Path">
/// The entry's path inside the snapshot, slash-separated; null for the root
/// tree and for a blob.
/// </param>
/// <param name="Blob">The blob a blob note is about.</param>
/// <param name="Record">The record a segment or metadata note is about.</param>
/// <param name="Outcome">The read outcome, or the entry's kind, as the code names it.</param>
/// <param name="Reason">Why an entry was refused, in the code's own words.</param>
/// <param name="Detail">What the reader or the platform said, which no type classifies.</param>
public sealed record RecoveryNote(
    RecoveryNoteKind Kind,
    string? Path = null,
    ObjectKey? Blob = null,
    ObjectId? Record = null,
    Enum? Outcome = null,
    string? Reason = null,
    string? Detail = null)
{
    private const string RootTree = "<root tree>";

    /// <summary>The note as the operator's terminal has always shown it.</summary>
    public override string ToString() => Kind switch
    {
        RecoveryNoteKind.BlobSkipped => $"blob '{Blob?.Value}' skipped: {Detail}",
        RecoveryNoteKind.Refused => $"FAILED {Path}: refused — {Reason}",
        RecoveryNoteKind.SkippedSpecial => $"skipped {Path}: {Outcome} materialisation is the full client's job",
        RecoveryNoteKind.SegmentMissing => $"FAILED {Path}: segment {Record} is in no readable blob",
        RecoveryNoteKind.SegmentUnreadable => $"FAILED {Path}: segment read {Outcome} — {Detail}",
        RecoveryNoteKind.HashMismatch => $"FAILED {Path}: the whole-file hash does not verify (FR-RST-002)",
        RecoveryNoteKind.RecordMissing => $"FAILED {Path ?? RootTree}: metadata record {Record} is in no readable blob",
        RecoveryNoteKind.RecordUnreadable => $"FAILED {Path ?? RootTree}: record read {Outcome} — {Detail}",
        _ => Kind.ToString(),
    };
}
