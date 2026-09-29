using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Agent;

/// <summary>
/// A capture source whose file content is read through the installation's
/// <c>background_read_limit</c> (NFR-PERF-013, ADR-0074). Scanning, probing
/// and revalidation pass through untouched — they stat, and a limit on the
/// disk's bandwidth is a limit on what is read, not on what is looked at.
/// </summary>
/// <remarks>
/// Built only for a background capture with a limit declared; a person's run
/// is given the source itself, which is what <see cref="Over"/> returns when
/// there is no limiter.
/// </remarks>
internal sealed class PacedFileSystemSource : IFileSystemSource
{
    private readonly IFileSystemSource _inner;
    private readonly ByteRateLimiter _limiter;

    private PacedFileSystemSource(IFileSystemSource inner, ByteRateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    /// <summary><paramref name="source"/> read through <paramref name="limiter"/>, or itself when there is none.</summary>
    public static IFileSystemSource Over(IFileSystemSource source, ByteRateLimiter? limiter)
    {
        ThrowHelper.ThrowIfNull(source);
        return limiter is null ? source : new PacedFileSystemSource(source, limiter);
    }

    /// <inheritdoc />
    public SourceFilesystemInfo Probe(string rootPath) => _inner.Probe(rootPath);

    /// <inheritdoc />
    public RevalidationProbe? Revalidate(ScanEntry entry) => _inner.Revalidate(entry);

    /// <inheritdoc />
    public IAsyncEnumerable<ScanEvent> ScanAsync(string rootPath, ScanOptions options, CancellationToken cancellationToken) =>
        _inner.ScanAsync(rootPath, options, cancellationToken);

    /// <inheritdoc />
    public Stream OpenRead(ScanEntry entry) => new PacedStream(_inner.OpenRead(entry), _limiter);

    /// <inheritdoc />
    public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
        new PacedStream(_inner.OpenAlternateStream(entry, streamName), _limiter);

    /// <inheritdoc />
    public ulong? DeviceOf(string path) => _inner.DeviceOf(path);
}
