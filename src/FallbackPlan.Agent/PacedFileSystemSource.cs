using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Filesystem;
using System.Runtime.CompilerServices;

namespace FallbackPlan.Agent;

/// <summary>
/// A capture source whose file content is read through the installation's
/// <c>background_read_limit</c> and <c>background_cpu_limit</c> (NFR-PERF-013,
/// ADR-0074, ADR-0200). The read limit leaves scanning, probing and
/// revalidation alone — they stat, and a limit on the disk's bandwidth is a
/// limit on what is read, not on what is looked at. The CPU limit looks at
/// each chunk read and, every <see cref="EventsPerLook"/> events, at the walk
/// itself (ADR-0200 Amendment 2): an incremental run of an unchanged tree reads
/// no content, and its walk is all the processor time it spends.
/// </summary>
/// <remarks>
/// Built only for a background capture with a limit declared; a person's run
/// is given the source itself, which is what <see cref="Over"/> returns when
/// there is no pace.
/// </remarks>
internal sealed class PacedFileSystemSource : IFileSystemSource
{
    private readonly IFileSystemSource _inner;
    private readonly BackgroundPace _pace;

    private PacedFileSystemSource(IFileSystemSource inner, BackgroundPace pace)
    {
        _inner = inner;
        _pace = pace;
    }

    /// <summary><paramref name="source"/> read through <paramref name="pace"/>, or itself when there is none.</summary>
    public static IFileSystemSource Over(IFileSystemSource source, BackgroundPace? pace)
    {
        ThrowHelper.ThrowIfNull(source);
        return pace is null ? source : new PacedFileSystemSource(source, pace);
    }

    /// <inheritdoc />
    public SourceFilesystemInfo Probe(string rootPath) => _inner.Probe(rootPath);

    /// <inheritdoc />
    public RevalidationProbe? Revalidate(ScanEntry entry) => _inner.Revalidate(entry);

    /// <summary>
    /// How many scan events pass between two looks at the CPU limit: often
    /// enough that a walk of an unchanged tree is held to the share, seldom
    /// enough that reading the process's processor time costs the walk little
    /// beside the stats it makes.
    /// </summary>
    public const int EventsPerLook = 64;

    /// <inheritdoc />
    public IAsyncEnumerable<ScanEvent> ScanAsync(string rootPath, ScanOptions options, CancellationToken cancellationToken) =>
        _pace.Cpu is { } cpu
            ? LookedAt(_inner.ScanAsync(rootPath, options, cancellationToken), cpu, cancellationToken)
            : _inner.ScanAsync(rootPath, options, cancellationToken);

    /// <inheritdoc />
    public Stream OpenRead(ScanEntry entry) => new PacedStream(_inner.OpenRead(entry), _pace);

    /// <inheritdoc />
    public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
        new PacedStream(_inner.OpenAlternateStream(entry, streamName), _pace);

    /// <inheritdoc />
    public ulong? DeviceOf(string path) => _inner.DeviceOf(path);

    public string? FreeAgain(ScanEntry entry) => _inner.FreeAgain(entry);

    private static async IAsyncEnumerable<ScanEvent> LookedAt(
        IAsyncEnumerable<ScanEvent> events, CpuLimiter cpu, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var passed = 0;
        await foreach (var scanEvent in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (++passed % EventsPerLook == 0)
            {
                await cpu.PaceAsync(cancellationToken).ConfigureAwait(false);
            }

            yield return scanEvent;
        }
    }
}
