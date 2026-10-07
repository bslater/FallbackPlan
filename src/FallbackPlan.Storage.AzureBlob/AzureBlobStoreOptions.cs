namespace FallbackPlan.Storage.AzureBlob;

/// <summary>How hard an Azure Blob store tries before a request is a fault (ADR-0093).</summary>
public sealed record AzureBlobStoreOptions
{
    /// <summary>The defaults: four attempts, a quarter of a second doubling between them.</summary>
    public static AzureBlobStoreOptions Default { get; } = new();

    /// <summary>
    /// How many times one request is sent before a transient refusal — a busy
    /// server, a server error, a connection that died — is reported as a
    /// fault. The scheduler's back-off is the retry after that (FR-QUOTA-001).
    /// </summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>The wait before the second attempt; each later one waits twice the last.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Where content that cannot be read twice is held while its digest is
    /// taken and it is sent; the system's temporary directory when null.
    /// </summary>
    public string? SpoolDirectory { get; init; }
}
