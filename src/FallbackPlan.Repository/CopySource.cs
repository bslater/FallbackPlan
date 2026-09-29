using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Somewhere a copy of a repository's blobs might be read from: the staging
/// archive, one of the set's destinations, or a peer's replica over the
/// retrieval session. A repair reads a sound copy of a damaged replica object
/// from one (FR-VER-007); a restore reads a record its own store would not
/// serve (FR-RST-007).
/// </summary>
/// <remarks>
/// Blobs are immutable and a replica holds them key for key, byte for byte
/// (specification 01 §4), so the same record sits at the same offset of the
/// same blob wherever that blob is held. That is what lets a record's one
/// location be read at any copy.
/// </remarks>
/// <param name="Name">How the copy is named to a person: "the staging archive", "destination 'spare'".</param>
/// <param name="OpenAsync">
/// Opens the copy's store, or answers null when it cannot be reached. Called
/// only once every earlier copy has failed to serve, because opening a peer's
/// means dialling it.
/// </param>
public sealed record CopySource(string Name, Func<CancellationToken, ValueTask<IObjectStore?>> OpenAsync);

/// <summary>Why a copy did not serve a record a restore asked it for (FR-RST-007).</summary>
public enum CopyFault
{
    /// <summary>
    /// The copy holds the blob and its bytes are not what was sealed: the
    /// record failed its checks there, or the blob's framing did. A finding
    /// about that copy.
    /// </summary>
    Damaged = 0,

    /// <summary>
    /// The copy would not read: an I/O fault. One attempt cannot tell a bad
    /// sector from a device going away, so this is not a finding
    /// (ADR-0035 Amendment 1).
    /// </summary>
    Unreadable = 1,

    /// <summary>
    /// The copy does not hold the record: its blob is not there — trimmed
    /// from a staging archive, or not yet delivered — or the blob's own
    /// footer does not list the record, which makes the location wrong
    /// rather than the copy.
    /// </summary>
    NotHeld = 2,

    /// <summary>The copy's store could not be opened or reached.</summary>
    Unreachable = 3,
}

/// <summary>One copy that did not serve a record, and why.</summary>
/// <param name="Source">The copy, as a person would name it.</param>
/// <param name="BlobKey">
/// The store key of the blob the record is in at that copy; null when the
/// copy could not say, because it does not hold the blob or was not reached.
/// </param>
/// <param name="Fault">Why it did not serve.</param>
/// <param name="Detail">What went wrong, in the reader's words.</param>
public sealed record CopyRefusal(string Source, string? BlobKey, CopyFault Fault, string Detail);

/// <summary>
/// A record read from another copy because the copies tried before it would
/// not serve it (FR-RST-007).
/// </summary>
/// <param name="ObjectId">The record.</param>
/// <param name="ReadFrom">The copy that served it, verified as the first would have been.</param>
/// <param name="PassedOver">The copies tried before it, in order, each with why it did not serve.</param>
public sealed record RecordReadAround(ObjectId ObjectId, string ReadFrom, IReadOnlyList<CopyRefusal> PassedOver);
