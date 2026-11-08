using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository;

/// <summary>
/// Which folders a run told what changed takes as its prior snapshot held
/// them, without walking them (ADR-0186), and what each brings.
/// </summary>
/// <remarks>
/// <para>
/// A folder is walked where it, or a folder below it, is named changed;
/// where the prior snapshot names a failure at or below it, so the file is
/// tried again; where the prior snapshot holds no folder there; and where a
/// tree in it was written as a chain, whose continuations no catalogue row
/// names. Every other folder is reused, and it stands for everything below
/// it. Nothing is reused where the prior snapshot was captured under other
/// rules or other folders, which no change source sees.
/// </para>
/// <para>
/// A run that walks everything takes no folder unwalked, but a folder its
/// cloud provider holds, whose listing did not come, keeps the tree the
/// prior snapshot held for it (ADR-0185 Amendment 6).
/// </para>
/// </remarks>
internal sealed class FolderReuse
{
    private readonly HashSet<string>? _walked;
    private readonly CatalogueGate _catalogue;
    private readonly ReadOnlyMemory<byte> _prior;
    private readonly Dictionary<string, CatalogueSubtree> _reused = new(StringComparer.Ordinal);

    private FolderReuse(HashSet<string>? walked, CatalogueGate catalogue, ReadOnlyMemory<byte> prior)
    {
        _walked = walked;
        _catalogue = catalogue;
        _prior = prior;
    }

    /// <summary>
    /// The reuse a job's changed folders allow, or null where the run walks
    /// everything: it was told nothing, has no prior snapshot or catalogue,
    /// the prior snapshot was captured under other rules or other folders,
    /// or what it was captured under, or failed to capture, cannot be read.
    /// </summary>
    public static async ValueTask<FolderReuse?> ForAsync(
        SnapshotJob job, CatalogueGate? catalogue, TargetedRecordReader? reader, CancellationToken cancellationToken)
    {
        if (job.ChangedFolders is not { } changed || catalogue is null || reader is null
            || await PriorAsync(job, catalogue, reader, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var (prior, manifest) = found;
        IReadOnlyList<string> failed = [];
        if (manifest.ErrorManifest is { } errors)
        {
            if (await reader.TryReadFailedPathsAsync(errors, cancellationToken).ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            failed = read;
        }

        // A folder named, and every folder above it. One already named has
        // its folders above it named too.
        var walked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changed.Concat(failed))
        {
            var at = path;
            while (walked.Add(at) && at.Length > 0)
            {
                var slash = at.LastIndexOf('/');
                at = slash < 0 ? string.Empty : at[..slash];
            }
        }

        return new FolderReuse(walked, catalogue, prior);
    }

    /// <summary>
    /// The prior snapshot's folders for a run that walks everything, which
    /// takes none unwalked, or null where it has none to give: no prior
    /// snapshot or catalogue, or one captured under other rules or other
    /// folders, or one that cannot be read (ADR-0185 Amendment 6).
    /// </summary>
    public static async ValueTask<FolderReuse?> ForWalkOfEverythingAsync(
        SnapshotJob job, CatalogueGate? catalogue, TargetedRecordReader? reader, CancellationToken cancellationToken) =>
        catalogue is not null && reader is not null
        && await PriorAsync(job, catalogue, reader, cancellationToken).ConfigureAwait(false) is { } found
            ? new FolderReuse(walked: null, catalogue, found.Prior)
            : null;

    private static async ValueTask<(ReadOnlyMemory<byte> Prior, SnapshotManifest Manifest)?> PriorAsync(
        SnapshotJob job, CatalogueGate catalogue, TargetedRecordReader reader, CancellationToken cancellationToken)
    {
        if (job.PriorSnapshotId is not { } prior
            || catalogue.Read(c => c.EnumerateSnapshots()).FirstOrDefault(row => row.SnapshotId.Span.SequenceEqual(prior.Span)) is not { } row
            || await reader.TryReadSnapshotAsync(row.ObjectId, cancellationToken).ConfigureAwait(false) is not { } manifest
            || await reader.TryReadPolicyAsync(manifest.PolicyManifest, cancellationToken).ConfigureAwait(false) is not { } policy
            || !CapturedAlike(policy, job))
        {
            return null;
        }

        return (prior, manifest);
    }

    /// <summary>How many folders the run reused without walking them.</summary>
    public int Reused { get; private set; }

    /// <summary>Whether the folder at <paramref name="path"/> is taken as the prior snapshot held it.</summary>
    public bool IsUnchanged(string path)
    {
        if (_walked is null)
        {
            return false;
        }

        if (_reused.ContainsKey(path))
        {
            return true;
        }

        if (_walked.Contains(path))
        {
            return false;
        }

        if (_catalogue.Read(c => c.ReadSubtree(_prior.Span, path, TreeChainWriter.DefaultShardBudget)) is not { } subtree)
        {
            _walked.Add(path);
            return false;
        }

        _reused[path] = subtree;
        return true;
    }

    /// <summary>
    /// Whether the prior snapshot holds a folder at <paramref name="path"/>,
    /// walked or not: a folder whose listing did not come keeps that tree,
    /// which <see cref="Take"/> then gives (ADR-0185 Amendment 6).
    /// </summary>
    public bool HeldBefore(string path)
    {
        if (_reused.ContainsKey(path))
        {
            return true;
        }

        if (_catalogue.Read(c => c.ReadSubtree(_prior.Span, path, TreeChainWriter.DefaultShardBudget)) is not { } subtree)
        {
            return false;
        }

        _reused[path] = subtree;
        return true;
    }

    /// <summary>The folder a walk did not enter, as the prior snapshot held it.</summary>
    public CatalogueSubtree Take(string path)
    {
        var reused = _reused.TryGetValue(path, out var found)
            ? found
            : throw new InvalidOperationException($"The walk passed over '{path}', which this run did not reuse.");
        Reused++;
        return reused;
    }

    /// <summary>
    /// Whether the prior snapshot was captured under this job's rules and
    /// folders: a folder the rules cut into, or a label that now names
    /// another folder, is one the prior snapshot holds otherwise.
    /// </summary>
    private static bool CapturedAlike(PolicyManifest policy, SnapshotJob job) =>
        policy.IncludeRules.SequenceEqual(job.IncludeRules, StringComparer.Ordinal)
        && policy.ExcludeRules.SequenceEqual(job.ExcludeRules, StringComparer.Ordinal)
        && policy.Roots.SequenceEqual(job.Roots.Select(static root => new RecordedRoot(root.Path, root.Label)));
}
