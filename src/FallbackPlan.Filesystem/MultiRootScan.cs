using System.Runtime.CompilerServices;
using System.Text;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Filesystem;

/// <summary>
/// Stitches several roots into one scan stream (ADR-0040): a synthetic root
/// directory brackets the whole walk, and each real root arrives as a
/// top-level directory named by its label — its own stat metadata kept, its
/// entries' relative paths prefixed with the label so rules, capture,
/// catalogue and prior-version lookup all speak one coordinate system. A
/// single root passes through untouched: the pre-multi-root shape, byte for
/// byte.
/// </summary>
public static class MultiRootScan
{
    /// <summary>Streams the roots' trees as one walk.</summary>
    /// <param name="source">The filesystem to walk.</param>
    /// <param name="roots">The capture roots; more than one requires labels.</param>
    /// <param name="options">Scanner switches; <see cref="ScanOptions.SubjectPrefix"/> is set per root.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The stitched event stream.</returns>
    public static async IAsyncEnumerable<ScanEvent> ScanAsync(
        IFileSystemSource source,
        IReadOnlyList<ScanRoot> roots,
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(roots);
        ThrowHelper.ThrowIfNull(options);
        ThrowHelper.ThrowIfZeroOrNegative(roots.Count);

        if (roots.Count == 1)
        {
            await foreach (var scanEvent in source.ScanAsync(
                roots[0].Path, options with { SubjectPrefix = null }, cancellationToken).ConfigureAwait(false))
            {
                yield return scanEvent;
            }

            yield break;
        }

        // The tree writer's MUST (06 §5) is ascending raw-name-byte order,
        // and the top-level names are the labels — so the roots walk in
        // raw-UTF-8 label order, not string order, which diverges above the
        // basic multilingual plane.
        var ordered = roots
            .Select(root => (Root: root, LabelBytes: Encoding.UTF8.GetBytes(
                root.Label ?? throw new ArgumentException(
                    $"root '{root.Path}' carries no label; a multi-root scan names every root", nameof(roots)))))
            .OrderBy(pair => pair.LabelBytes, RawByteOrder.Instance)
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].LabelBytes.AsSpan().SequenceEqual(ordered[i].LabelBytes))
            {
                throw new ArgumentException(
                    $"two roots share the label '{ordered[i].Root.Label}'", nameof(roots));
            }
        }

        // The synthetic root: a directory that exists only as the snapshot's
        // frame. Directories are never opened for content, so the empty
        // FullPath is inert; the metadata is honestly empty because nothing
        // was observed (the precedent is the single-stream path's root).
        var synthetic = new ScanEntry
        {
            RelativePath = string.Empty,
            NameBytes = "/"u8.ToArray(),
            NameNormalisation = NameNormalisation.Unknown,
            Kind = ScanEntryKind.Directory,
            Length = 0,
            Metadata = EntryMetadata.Empty,
            FullPath = string.Empty,
        };

        yield return new ScanEvent.EnterDirectory(synthetic);

        foreach (var (root, labelBytes) in ordered)
        {
            var label = root.Label!;
            await foreach (var scanEvent in source.ScanAsync(
                root.Path, options with { SubjectPrefix = label }, cancellationToken).ConfigureAwait(false))
            {
                yield return scanEvent switch
                {
                    ScanEvent.EnterDirectory { Entry.RelativePath.Length: 0 } enter =>
                        new ScanEvent.EnterDirectory(Relabel(enter.Entry, label, labelBytes)),
                    ScanEvent.LeaveDirectory { Entry.RelativePath.Length: 0 } leave =>
                        new ScanEvent.LeaveDirectory(Relabel(leave.Entry, label, labelBytes)),
                    ScanEvent.EnterDirectory enter =>
                        new ScanEvent.EnterDirectory(Prefix(enter.Entry, label)),
                    ScanEvent.LeaveDirectory leave =>
                        new ScanEvent.LeaveDirectory(Prefix(leave.Entry, label)),
                    ScanEvent.Leaf leaf => new ScanEvent.Leaf(Prefix(leaf.Entry, label)),
                    ScanEvent.Failure failure => new ScanEvent.Failure(failure.Detail with
                    {
                        RelativePath = failure.Detail.RelativePath.Length == 0
                            ? label
                            : label + "/" + failure.Detail.RelativePath,
                    }),
                    _ => scanEvent,
                };
            }
        }

        yield return new ScanEvent.LeaveDirectory(synthetic);

        static ScanEntry Relabel(ScanEntry entry, string label, byte[] labelBytes) => entry with
        {
            RelativePath = label,
            NameBytes = labelBytes,
            // The label is validated NFC upstream; saying so keeps the tree
            // manifest's name statement honest rather than "unknown".
            NameNormalisation = NameNormalisation.Nfc,
        };

        static ScanEntry Prefix(ScanEntry entry, string label) => entry with
        {
            RelativePath = label + "/" + entry.RelativePath,
        };
    }

    /// <summary>Raw-byte lexicographic order — the tree writer's own.</summary>
    private sealed class RawByteOrder : IComparer<byte[]>
    {
        public static readonly RawByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x!.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>
/// The one filesystem statement a snapshot may make about several roots
/// (ADR-0040): the manifest's <c>source_filesystem</c> is singular and
/// signed, so a multi-root publication records the conservative
/// intersection — the claims that hold for every root.
/// </summary>
public static class SourceFilesystemIntersection
{
    /// <summary>Intersects per-root probes into one conservative record.</summary>
    /// <param name="probes">One probe per root; at least one.</param>
    /// <returns>The intersection.</returns>
    public static SourceFilesystemInfo Intersect(IReadOnlyList<SourceFilesystemInfo> probes)
    {
        ThrowHelper.ThrowIfNull(probes);
        ThrowHelper.ThrowIfZeroOrNegative(probes.Count);

        if (probes.Count == 1)
        {
            return probes[0];
        }

        return new SourceFilesystemInfo(
            // Case-insensitivity anywhere makes case a hazard everywhere the
            // rules apply, so any insensitive root turns the whole set
            // insensitive — the direction that never mistakes two names for
            // two files.
            CaseSensitive: probes.All(probe => probe.CaseSensitive),
            SupportsSparse: probes.All(probe => probe.SupportsSparse),
            Name: string.Join("+", probes.Select(probe => probe.Name).Distinct(StringComparer.Ordinal)),
            MaxPathBytes: MinOrNull(probes.Select(probe => probe.MaxPathBytes)),
            MaxComponentBytes: MinOrNull(probes.Select(probe => probe.MaxComponentBytes)),
            ReservedNames: probes.Any(probe => probe.ReservedNames == true) ? true
                : probes.Any(probe => probe.ReservedNames == false) ? false
                : null);
    }

    private static uint? MinOrNull(IEnumerable<uint?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToList();
        return known.Count == 0 ? null : known.Min();
    }
}
