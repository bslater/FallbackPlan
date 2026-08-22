namespace FallbackPlan.Domain;

/// <summary>
/// The repository format's hard limits (specification 00 §8). A limit is
/// enforced <em>before</em> allocating, and an object that exceeds one is a
/// damage finding, not merely a rejected input — a conforming writer cannot
/// have produced it.
/// </summary>
public static class FormatLimits
{
    /// <summary>The repository format version this implementation writes.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>
    /// The write-only repository format version (ADR-0042): file contents
    /// sealed to the repository's public key, structure symmetric. Chosen at
    /// repository creation, never converted to or from.
    /// </summary>
    public const ushort SealedFormatVersion = 2;

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

    /// <summary>Maximum CBOR length of the wrapped key bundle: 4 096 bytes.</summary>
    public const int MaxKeyBundleCborLength = 4_096;

    /// <summary>Maximum segment references in one file-version manifest: 1 048 576.</summary>
    public const int MaxSegmentReferencesPerManifest = 1_048_576;

    /// <summary>Maximum length of one path component: 1 024 bytes.</summary>
    public const int MaxPathComponentLength = 1_024;

    /// <summary>Maximum path depth: 512 components.</summary>
    public const int MaxPathDepth = 512;
}
