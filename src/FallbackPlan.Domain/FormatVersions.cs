namespace FallbackPlan.Domain;

/// <summary>
/// The repository format versions this implementation knows, and the
/// questions the code asks of one (specification 00 §5;
/// [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1). Every site that used to compare a version against one
/// number asks a named question here instead, so that admitting a third
/// version is a change to one file rather than a hunt for every
/// <c>&gt;=</c>.
/// </summary>
public static class FormatVersions
{
    /// <summary>
    /// The symmetric container stamp: a format-2 repository's metadata blobs
    /// and every standalone record (ADR-0014 Amendment 1). Not a repository
    /// format a descriptor may carry — format 1 is withdrawn.
    /// </summary>
    public const ushort Symmetric = 1;

    /// <summary>Format 2: file content sealed to the repository's public key per blob (ADR-0042).</summary>
    public const ushort SealedDataPlane = 2;

    /// <summary>
    /// Format 3: a sealed record stops encoding where it lives — its key is
    /// the object's, its nonce is carried, its associated data omits the
    /// ordinal, and a data record's sealed key rides its own prefix (ADR-0052).
    /// </summary>
    public const ushort RelocatableRecords = 3;

    /// <summary>
    /// Format 4: every delta carries an index generation, one more than the
    /// highest its writer had seen, and entries rank by it (07 §2 key 12,
    /// §3). Records and blobs are format 3's (ADR-0096).
    /// </summary>
    public const ushort IndexGenerations = 4;

    /// <summary>
    /// Format 5: a checkpoint carries the deltas it subsumes as their stored
    /// objects (07 §5 key 10), and a loader reads a carried delta from the
    /// checkpoint rather than from its key. Deltas are format 4's, records
    /// and blobs format 3's (ADR-0097).
    /// </summary>
    public const ushort DeltaCheckpoints = 5;

    /// <summary>Whether a descriptor's version is one this implementation reads.</summary>
    public static bool IsReadable(ushort formatVersion) =>
        formatVersion is >= SealedDataPlane and <= FormatLimits.LatestFormatVersion;

    /// <summary>
    /// Whether this implementation may write a repository whose effective
    /// version is <paramref name="formatVersion"/>: one it implements
    /// (specification 11 §5.3). A repository a newer build upgraded past
    /// <see cref="FormatLimits.LatestFormatVersion"/> is still read, because
    /// every object declares its own container and every index object its own
    /// keys, but what an object written at that version must carry is the
    /// newer build's to say.
    /// </summary>
    public static bool IsWritable(ushort formatVersion) =>
        formatVersion is >= SealedDataPlane and <= FormatLimits.LatestFormatVersion;

    /// <summary>
    /// Whether a blob of this version and class carries one sealed content
    /// key in its envelope for every record in it (05 §2.1) — format 2's data
    /// blobs, and nothing else.
    /// </summary>
    public static bool SealsContentPerBlob(ushort formatVersion, bool dataClass) =>
        formatVersion == SealedDataPlane && dataClass;

    /// <summary>
    /// Whether a blob of this version and class carries a sealed key in each
    /// record's prefix (05 §2.2) — format 3's data blobs.
    /// </summary>
    public static bool SealsContentPerRecord(ushort formatVersion, bool dataClass) =>
        formatVersion >= RelocatableRecords && dataClass;

    /// <summary>Whether a blob's record payloads are sealed to the public key at all, however carried.</summary>
    public static bool SealsContent(ushort formatVersion, bool dataClass) =>
        SealsContentPerBlob(formatVersion, dataClass) || SealsContentPerRecord(formatVersion, dataClass);

    /// <summary>
    /// Whether a container's records are format-3 records: keyed to the
    /// object, nonce carried in the prefix, 51-byte associated data (04 §2–§4).
    /// </summary>
    public static bool HasRelocatableRecords(ushort formatVersion) => formatVersion >= RelocatableRecords;

    /// <summary>
    /// Whether a writer at this version stamps each delta with an index
    /// generation (07 §2 key 12). Below format 4 it must not: a reader that
    /// predates the key refuses a delta carrying it.
    /// </summary>
    public static bool HasIndexGenerations(ushort formatVersion) => formatVersion >= IndexGenerations;

    /// <summary>
    /// Whether a writer at this version may publish a checkpoint carrying
    /// deltas (07 §5 key 10). Below format 5 it must not: a reader that
    /// predates the key refuses the checkpoint and reports it as damage.
    /// </summary>
    public static bool HasDeltaCheckpoints(ushort formatVersion) => formatVersion >= DeltaCheckpoints;

    /// <summary>
    /// The version a blob of this class is stamped with inside a repository
    /// of <paramref name="repositoryFormatVersion"/> — which is not the same
    /// number, and the one place that says so.
    /// </summary>
    /// <remarks>
    /// A format-2 repository writes <see cref="Symmetric"/> metadata blobs
    /// and <see cref="SealedDataPlane"/> data blobs, because the symmetric
    /// construction is the one format 1 defined and format 2 kept byte for
    /// byte (ADR-0014 Amendment 1); a format-3 repository writes
    /// <see cref="RelocatableRecords"/> for both, and so do format-4 and
    /// format-5 ones, whose changes are to the index alone. The stamp is AAD input, so
    /// answering this wrongly does not produce a warning — it produces a blob
    /// whose own repository cannot open it.
    /// </remarks>
    /// <param name="repositoryFormatVersion">The descriptor's format version.</param>
    /// <param name="dataClass">Whether the blob is a data blob.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The repository is at a version this implementation does not write, whose containers it cannot name.
    /// </exception>
    public static ushort ContainerVersion(ushort repositoryFormatVersion, bool dataClass)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(repositoryFormatVersion, FormatLimits.LatestFormatVersion);

        if (HasRelocatableRecords(repositoryFormatVersion))
        {
            return RelocatableRecords;
        }

        return dataClass ? SealedDataPlane : Symmetric;
    }
}
