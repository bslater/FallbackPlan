namespace FallbackPlan.Domain;

/// <summary>
/// The repository format's hard limits (specification 00 §8). A limit is
/// enforced <em>before</em> allocating, and an object that exceeds one is a
/// damage finding, not merely a rejected input — a conforming writer cannot
/// have produced it.
/// </summary>
public static class FormatLimits
{
    /// <summary>
    /// The repository format version a new repository is created with unless
    /// told otherwise: file contents sealed to the repository's public key,
    /// structure symmetric (ADR-0042). The descriptor carries it, and so does
    /// every sealed data blob. Format 1 was withdrawn before any freeze; the
    /// number is not renumbered, because it is bound into every descriptor
    /// and every sealed blob's AAD already on disk. Readers accept every
    /// version from this one to <see cref="LatestFormatVersion"/>
    /// (<see cref="FormatVersions.IsReadable"/>).
    /// </summary>
    public const ushort FormatVersion = FormatVersions.SealedDataPlane;

    /// <summary>
    /// The newest repository format this implementation reads and, on
    /// request, writes: format 3, whose records are relocatable
    /// ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)).
    /// Creation stays at <see cref="FormatVersion"/> unless a caller asks for
    /// this one; a repository's version is fixed at creation.
    /// </summary>
    public const ushort LatestFormatVersion = FormatVersions.RelocatableRecords;

    /// <summary>
    /// The version stamped on a <b>symmetric</b> container — a metadata blob
    /// or a standalone record — inside a format-2 repository. The symmetric
    /// construction is the one format 1 defined and format 2 kept byte for
    /// byte (specification 03 §9), and the stamp is AAD, so this is a fact
    /// about bytes on disk rather than a choice: a reader deriving the wrong
    /// value opens nothing.
    /// </summary>
    public const ushort SymmetricFormatVersion = 1;

    /// <summary>Maximum stored (ciphertext) length of one record: 64 MiB.</summary>
    public const int MaxRecordStoredLength = 64 * 1024 * 1024;

    /// <summary>Maximum number of records in one blob: 65 536.</summary>
    public const int MaxRecordsPerBlob = 65_536;

    /// <summary>Maximum size of one blob: 512 MiB.</summary>
    public const long MaxBlobSize = 512L * 1024 * 1024;

    /// <summary>Maximum size of one metadata object: 16 MiB.</summary>
    public const int MaxMetadataObjectSize = 16 * 1024 * 1024;

    /// <summary>Maximum CBOR body length of the repository descriptor: 65 536 bytes.</summary>
    public const int MaxDescriptorCborLength = 65_536;

    /// <summary>Maximum segment references in one file-version manifest: 1 048 576.</summary>
    public const int MaxSegmentReferencesPerManifest = 1_048_576;

    /// <summary>Maximum length of one path component: 1 024 bytes.</summary>
    public const int MaxPathComponentLength = 1_024;

    /// <summary>Maximum path depth: 512 components.</summary>
    public const int MaxPathDepth = 512;
}
