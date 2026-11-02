using FallbackPlan.Domain;
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

    /// <summary>A file or a link the output would not take at its name, failed alone (ADR-0155).</summary>
    WriteFailed = 8,

    /// <summary>A later name of a hard-link group restored as a file of its own, and why (ADR-0156).</summary>
    NotLinked = 9,

    /// <summary>A file's alternate stream not written back, and why; the file still restored (ADR-0174 Amendment 1).</summary>
    StreamNotWritten = 10,

    /// <summary>The flags a file or a link was captured with, which no restore sets; the item still restored (ADR-0180 Amendment 2).</summary>
    FlagsNotApplied = 11,

    /// <summary>A link captured as a Windows junction, made a symbolic link to the same path where the platform makes no junction; it still restored (ADR-0183 Amendment 3).</summary>
    JunctionMadeAsALink = 12,
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
        RecoveryNoteKind.WriteFailed => $"FAILED {Path}: the output would not take it — {Detail}",
        RecoveryNoteKind.NotLinked => $"{Path}: restored as a file of its own, not linked to its group — {Reason}",
        RecoveryNoteKind.StreamNotWritten => $"{Path}: alternate stream {Reason}",
        RecoveryNoteKind.FlagsNotApplied => $"{Path}: {Reason}",
        RecoveryNoteKind.JunctionMadeAsALink => $"{Path}: {MetadataRules.JunctionMadeAsALink}",
        _ => Kind.ToString(),
    };
}
