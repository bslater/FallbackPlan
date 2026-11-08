using FallbackPlan.Filesystem;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The real source, with the files named <c>.cloud</c> online-only, and
/// their fetch failing as a provider's does while it does not deliver. A
/// folder named <c>.cloud</c> is one whose listing its provider keeps: while
/// it does not deliver, the walk names the folder unlisted, as it does a
/// provider's folder whose listing does not come (ADR-0185 Amendment 6). No
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
        var withheld = 0;
        await foreach (var scanEvent in inner.ScanAsync(rootPath, options, cancellationToken).ConfigureAwait(false))
        {
            // Inside a folder whose listing did not come, nothing follows.
            if (withheld > 0)
            {
                withheld += scanEvent switch
                {
                    ScanEvent.EnterDirectory => 1,
                    ScanEvent.LeaveDirectory => -1,
                    _ => 0,
                };
                continue;
            }

            if (scanEvent is ScanEvent.EnterDirectory enter && IsCloud(enter.Entry) && !delivers())
            {
                withheld = 1;
                yield return new ScanEvent.UnlistedDirectory(
                    enter.Entry, new ScanFailure(enter.Entry.RelativePath, Domain.CaptureFailureReason.IoError, TimedOut));
                continue;
            }

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
