using System.Text;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Format.Manifests;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository;

/// <summary>One comparison bucket: the exact count, and a bounded sample of the paths it counted.</summary>
/// <param name="Count">Every file the bucket matched — always exact, never truncated.</param>
/// <param name="Sample">The first paths encountered, capped by the caller's sample limit.</param>
public sealed record SourceChangeBucket(long Count, IReadOnlyList<string> Sample);

/// <summary>
/// What the live source holds now versus what the baseline snapshot recorded
/// (FR-SVC-009). Deletion has two honest faces and they are kept apart:
/// <see cref="Deleted"/> is a file the disk lost; <see cref="NoLongerIncluded"/>
/// is a file the rules stopped capturing — it leaves future snapshots because
/// of a configuration change, not because anything happened to it.
/// </summary>
/// <param name="Unchanged">Files identical in content and metadata at their recorded path.</param>
/// <param name="Failures">Paths the walk could not read.</param>
/// <param name="New">Files the baseline snapshot does not hold.</param>
/// <param name="Updated">Files whose content changed.</param>
/// <param name="MetadataOnly">Files whose bytes are unchanged but whose metadata moved — a chmod, an owner change.</param>
/// <param name="Moved">Files found at a different path by stable identity; their old paths are not double-counted deleted.</param>
/// <param name="Deleted">Files the baseline holds, still captured by the rules, absent from disk.</param>
/// <param name="NoLongerIncluded">Files the baseline holds that the rules no longer capture.</param>
public sealed record SourceComparison(
    long Unchanged,
    long Failures,
    SourceChangeBucket New,
    SourceChangeBucket Updated,
    SourceChangeBucket MetadataOnly,
    SourceChangeBucket Moved,
    SourceChangeBucket Deleted,
    SourceChangeBucket NoLongerIncluded);

/// <summary>
/// Walks the live source under a set's rules and diffs it against a snapshot
/// — a dry scan: no content is opened, nothing is written or published
/// (FR-SVC-009). The classification reuses <see cref="ChangeDetection"/>'s
/// predicates verbatim, so what this reports and what the next backup decides
/// are one judgement.
/// </summary>
public static class SourceComparer
{
    /// <summary>Compares the source trees under <paramref name="roots"/> with a baseline snapshot.</summary>
    /// <param name="source">The filesystem to walk.</param>
    /// <param name="roots">The capture roots; several walk exactly as a multi-root publication would (ADR-0040).</param>
    /// <param name="includeRules">rules-v1 include rules (specification 06 §7.1).</param>
    /// <param name="excludeRules">rules-v1 exclude rules.</param>
    /// <param name="catalogue">The set's catalogue, opened read-side; the caller owns it. Null compares against nothing.</param>
    /// <param name="baselineSnapshotId">The snapshot to diff against; null means an empty baseline — everything reads as new.</param>
    /// <param name="sampleLimit">The most paths any bucket carries; counts stay exact past it.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The classified comparison.</returns>
    /// <exception cref="ArgumentException">The rules are invalid — the same refusal a writer gives them.</exception>
    public static async ValueTask<SourceComparison> CompareAsync(
        IFileSystemSource source,
        IReadOnlyList<ScanRoot> roots,
        IReadOnlyList<string> includeRules,
        IReadOnlyList<string> excludeRules,
        CatalogueDb? catalogue,
        ReadOnlyMemory<byte>? baselineSnapshotId,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(roots);
        ThrowHelper.ThrowIfZeroOrNegative(roots.Count);
        ThrowHelper.ThrowIfNull(includeRules);
        ThrowHelper.ThrowIfNull(excludeRules);

        // Probe first: rule case-sensitivity is the filesystem's, exactly as
        // publication compiles them (specification 06 §7.1: writers MUST
        // refuse invalid rules, and a preview must refuse what a writer
        // would). Several roots intersect conservatively, as publication does.
        var filesystem = roots.Count == 1
            ? source.Probe(roots[0].Path)
            : SourceFilesystemIntersection.Intersect([.. roots.Select(root => source.Probe(root.Path))]);
        if (!PathRuleSet.TryCreate(
                includeRules, excludeRules, caseSensitive: filesystem.CaseSensitive,
                out var rules, out var ruleDefects))
        {
            throw new ArgumentException(
                "The capture rules are invalid and a writer MUST refuse them (specification 06 §7.1): " +
                string.Join("; ", ruleDefects),
                nameof(includeRules));
        }

        var limit = Math.Max(0, sampleLimit);
        var comparer = filesystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var byPath = new Dictionary<string, CatalogueTreeEntry>(comparer);
        var byIdentity = new Dictionary<(ulong Device, ulong FileId), CatalogueTreeEntry>();
        var consumed = new HashSet<string>(comparer);

        if (catalogue is not null && baselineSnapshotId is { } baseline)
        {
            foreach (var entry in catalogue.EnumerateLeaves(baseline.Span))
            {
                byPath[entry.Path] = entry;
                if (entry is { IdentityDevice: { } device, IdentityFileId: { } fileId })
                {
                    // Last-wins on a shared identity: a hardlink group's two
                    // names are one inode, and either answer is as honest as
                    // the other — the path index still holds both names.
                    byIdentity[(device, fileId)] = entry;
                }
            }
        }

        long unchanged = 0;
        long failures = 0;
        var added = new BucketBuilder(limit);
        var updated = new BucketBuilder(limit);
        var metadataOnly = new BucketBuilder(limit);
        var moved = new BucketBuilder(limit);

        await foreach (var scanEvent in MultiRootScan.ScanAsync(
            source, roots, new ScanOptions { Rules = rules }, cancellationToken).ConfigureAwait(false))
        {
            switch (scanEvent)
            {
                case ScanEvent.Leaf leaf:
                    // The other half of the dialect, applied where publication
                    // applies it: exclusions pruned the walk, includes decide
                    // capture per leaf on the NFC spelling of the name.
                    if (rules is { } compiled && !compiled.IsCaptured(
                            leaf.Entry.RelativePath.Normalize(NormalizationForm.FormC)))
                    {
                        break;
                    }

                    Classify(leaf.Entry);
                    break;

                case ScanEvent.Failure:
                    failures++;
                    break;

                default:
                    break; // directory brackets carry no per-file judgement
            }
        }

        // What the baseline still holds that the walk never consumed: gone
        // from disk, or merely gone from the rules — the two are different
        // findings and are reported apart.
        var deleted = new BucketBuilder(limit);
        var noLongerIncluded = new BucketBuilder(limit);
        foreach (var entry in byPath.Values)
        {
            if (consumed.Contains(entry.Path))
            {
                continue;
            }

            if (rules is { } compiled && !compiled.IsCaptured(entry.Path.Normalize(NormalizationForm.FormC)))
            {
                noLongerIncluded.Add(entry.Path);
            }
            else
            {
                deleted.Add(entry.Path);
            }
        }

        return new SourceComparison(
            unchanged,
            failures,
            added.Build(),
            updated.Build(),
            metadataOnly.Build(),
            moved.Build(),
            deleted.Build(),
            noLongerIncluded.Build());

        void Classify(ScanEntry entry)
        {
            if (byPath.TryGetValue(entry.RelativePath, out var prior))
            {
                consumed.Add(prior.Path);
                if (ChangeDetection.IsContentUnchanged(entry, prior))
                {
                    if (ChangeDetection.IsMetadataUnchanged(
                            FileVersionManifestCodec.MetadataDigest(entry.Metadata), prior))
                    {
                        unchanged++;
                    }
                    else
                    {
                        metadataOnly.Add(entry.RelativePath);
                    }
                }
                else
                {
                    updated.Add(entry.RelativePath);
                }

                return;
            }

            if (entry.Identity is { } identity
                && byIdentity.TryGetValue((identity.Device, identity.FileId), out var priorByIdentity)
                && !consumed.Contains(priorByIdentity.Path))
            {
                // The same inode at another name: a rename, not a delete plus
                // a create (architecture 06 §1). Its old path is consumed so
                // the post-walk pass does not also count it deleted.
                consumed.Add(priorByIdentity.Path);
                moved.Add(entry.RelativePath);
                return;
            }

            added.Add(entry.RelativePath);
        }
    }

    /// <summary>Accumulates one bucket: the exact count and the first paths up to the cap.</summary>
    private sealed class BucketBuilder(int limit)
    {
        private readonly List<string> _sample = [];
        private long _count;

        public void Add(string path)
        {
            _count++;
            if (_sample.Count < limit)
            {
                _sample.Add(path);
            }
        }

        public SourceChangeBucket Build() => new(_count, _sample);
    }
}
