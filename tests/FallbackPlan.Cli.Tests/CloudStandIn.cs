using FallbackPlan.Filesystem;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The real source, with the files named <c>.cloud</c> online-only, and
/// their fetch failing as a provider's does while it does not deliver. No
/// runner has a cloud provider, so a test wraps a direct backup's source in
/// this (ADR-0194), as the service's tests wrap the service's.
/// </summary>
internal sealed class CloudStandIn(IFileSystemSource inner, Func<bool> delivers) : IFileSystemSource
{
    /// <summary>What a provider says when it does not deliver in time.</summary>
    public const string TimedOut = "The cloud operation was not completed before the time-out period expired.";

    public SourceFilesystemInfo Probe(string rootPath) => inner.Probe(rootPath);

    public RevalidationProbe? Revalidate(ScanEntry entry) => inner.Revalidate(entry);

    public async IAsyncEnumerable<ScanEvent> ScanAsync(
        string rootPath, ScanOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var scanEvent in inner.ScanAsync(rootPath, options, cancellationToken).ConfigureAwait(false))
        {
            yield return scanEvent is ScanEvent.Leaf leaf && IsCloud(leaf.Entry)
                ? new ScanEvent.Leaf(leaf.Entry with { OnlineOnly = true })
                : scanEvent;
        }
    }

    public Stream OpenRead(ScanEntry entry) =>
        IsCloud(entry) && !delivers() ? throw new IOException(TimedOut) : inner.OpenRead(entry);

    public Stream OpenAlternateStream(ScanEntry entry, string streamName) => inner.OpenAlternateStream(entry, streamName);

    public ulong? DeviceOf(string path) => inner.DeviceOf(path);

    // Nothing here is a provider's to free.
    public string? FreeAgain(ScanEntry entry) => null;

    private static bool IsCloud(ScanEntry entry) => entry.RelativePath.EndsWith(".cloud", StringComparison.Ordinal);
}
