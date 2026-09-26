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
    /// told otherwise: format 3, whose records carry their own nonce and
    /// sealed key and can therefore be relocated between blobs without being
    /// opened ([ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)).
    /// The descriptor carries it, and so does every blob.
    /// </summary>
    /// <remarks>
    /// It equals <see cref="LatestFormatVersion"/>, and that is the rule
    /// rather than a coincidence: a product that can read a format creates at
    /// it, or the format reaches nobody. The two constants stay separate
    /// because they answer different questions — what a reader accepts, and
    /// what a writer makes — and a build that ever needs them to differ is
    /// taking a decision, not inheriting one. An existing repository is not
    /// migrated by this: its version is fixed at creation and moves only
    /// under a signed upgrade record.
    /// </remarks>
    public const ushort FormatVersion = FormatVersions.RelocatableRecords;

    /// <summary>
    /// The newest repository format this implementation reads: format 3.
    /// Readers accept every version from <see cref="FormatVersions.SealedDataPlane"/>
    /// to this one (<see cref="FormatVersions.IsReadable"/>), so a format-2
    /// repository written before the default moved is read in place and never
    /// rewritten. Format 1 was withdrawn before any freeze; the numbers are
    /// not renumbered, because they are bound into every descriptor and every
    /// sealed blob's AAD already on disk.
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
