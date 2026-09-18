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

    /// <summary>Whether a descriptor's version is one this implementation reads.</summary>
    public static bool IsReadable(ushort formatVersion) =>
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
}
