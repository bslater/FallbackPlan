using System.Runtime.CompilerServices;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A deterministic in-memory filesystem honouring the scanner contract —
/// depth-first, byte-sorted, rules-pruned — so publication semantics are
/// testable on every platform: symlinks, specials, hardlinks, sparse
/// extents, alternate streams, injected failures, and mid-read mutation
/// need no OS cooperation here.
/// </summary>
internal sealed class FakeFileSystemSource : IFileSystemSource
{
    internal sealed record Node
    {
        public required string RelativePath { get; init; }

        public ScanEntryKind Kind { get; init; } = ScanEntryKind.File;

        public byte[] Content { get; set; } = [];

        public ScanIdentity? Identity { get; init; }

        public EntryMetadata Metadata { get; set; } = EntryMetadata.Empty;

        public byte[]? LinkTarget { get; init; }

        public IReadOnlyList<SparseExtent> SparseExtents { get; init; } = [];

        public Dictionary<string, byte[]> AlternateStreams { get; init; } = [];

        public IReadOnlyList<string> Diagnostics { get; init; } = [];

        /// <summary>When set, opening this node's content throws.</summary>
        public Exception? OpenFailure { get; set; }

        /// <summary>How many times revalidation still reports "changed" before settling.</summary>
        public int RevalidationChangesRemaining { get; set; }

        /// <summary>
        /// When set, revalidation reports this identity instead of the
        /// node's — the name now refers to a different object, which is a
        /// substitution rather than an edit.
        /// </summary>
        public ScanIdentity? SubstitutedIdentity { get; set; }
    }

    private readonly Dictionary<string, Node> _nodes = [];
    private ulong _nextFileId = 100;

    public SourceFilesystemInfo Info { get; init; } = new(
        CaseSensitive: true,
        SupportsSparse: true,
        Name: "fakefs",
        MaxPathBytes: 4096,
        MaxComponentBytes: 255,
        ReservedNames: false);

    /// <summary>The scan failures to inject verbatim into the event stream.</summary>
    public List<ScanFailure> InjectedFailures { get; } = [];

    /// <summary>Every path whose content was opened — the short-circuit oracle (NFR-PERF-003).</summary>
    public List<string> OpenedPaths { get; } = [];

    public Node AddFile(string relativePath, byte[] content, uint linkCount = 1, ulong? fileId = null)
    {
        var node = new Node
        {
            RelativePath = relativePath,
            Content = content,
            Identity = new ScanIdentity(Device: 7, fileId ?? _nextFileId++, linkCount),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4 },
        };
        _nodes[relativePath] = node;
        return node;
    }

    public Node AddNode(Node node)
    {
        _nodes[node.RelativePath] = node;
        return node;
    }

    /// <summary>Drops an entry — the user deleted the file between two backups.</summary>
    /// <param name="relativePath">The path that is gone.</param>
    /// <returns><see langword="true"/> when it was there to remove.</returns>
    public bool Remove(string relativePath) => _nodes.Remove(relativePath);

    public SourceFilesystemInfo Probe(string rootPath) => Info;

    public RevalidationProbe? Revalidate(ScanEntry entry)
    {
        if (!_nodes.TryGetValue(entry.FullPath, out var node))
        {
            return null;
        }

        if (node.RevalidationChangesRemaining > 0)
        {
            node.RevalidationChangesRemaining--;
            return new RevalidationProbe(
                node.Content.Length, (node.Metadata.ModifiedAt ?? 0) + 999,
                node.SubstitutedIdentity ?? node.Identity);
        }

        return new RevalidationProbe(
            node.Content.Length, node.Metadata.ModifiedAt, node.SubstitutedIdentity ?? node.Identity);
    }

    public async IAsyncEnumerable<ScanEvent> ScanAsync(
        string rootPath, ScanOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var root = new ScanEntry
        {
            RelativePath = string.Empty,
            NameBytes = ReadOnlyMemory<byte>.Empty,
            NameNormalisation = NameNormalisation.Nfc,
            Kind = ScanEntryKind.Directory,
            Length = 0,
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
            FullPath = rootPath,
        };

        yield return new ScanEvent.EnterDirectory(root);

        // A root other than "/" scopes the walk to that subtree, with
        // relative paths stripped of it — how a multi-root job sees several
        // subtrees of one fake through separate per-root scans.
        var start = rootPath is "/" or "" ? string.Empty : rootPath.Trim('/');
        var strip = start.Length == 0 ? 0 : start.Length + 1;
        foreach (var scanEvent in WalkDirectory(start, strip, options))
        {
            yield return scanEvent;
        }

        foreach (var failure in InjectedFailures)
        {
            yield return new ScanEvent.Failure(failure);
        }

        yield return new ScanEvent.LeaveDirectory(root);
    }

    private IEnumerable<ScanEvent> WalkDirectory(string directory, int strip, ScanOptions options)
    {
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";

        var children = _nodes.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
            .Select(path =>
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/', StringComparison.Ordinal);
                return slash < 0 ? rest : rest[..slash];
            })
            .Distinct()
            .OrderBy(name => Encoding.UTF8.GetBytes(name), ByteArrayComparer.Instance)
            .ToList();

        foreach (var name in children)
        {
            var childPath = prefix + name;
            var relativePath = childPath[strip..];

            // The same subject spelling the real scanner uses (ADR-0040): the
            // label prefix joins for the rules' benefit only.
            var subject = options.SubjectPrefix is null ? relativePath : options.SubjectPrefix + "/" + relativePath;

            if (options.Rules is { } rules && rules.IsExcluded(subject))
            {
                continue;
            }

            if (_nodes.TryGetValue(childPath, out var node))
            {
                yield return new ScanEvent.Leaf(new ScanEntry
                {
                    RelativePath = relativePath,
                    NameBytes = Encoding.UTF8.GetBytes(name),
                    NameNormalisation = NameNormalisation.Nfc,
                    Kind = node.Kind,
                    Length = node.Content.Length,
                    Identity = node.Identity,
                    Metadata = node.Metadata,
                    LinkTarget = node.LinkTarget,
                    SparseExtents = node.SparseExtents,
                    AlternateStreamNames = node.AlternateStreams
                        .Select(pair => (pair.Key, (long)pair.Value.Length)).ToList(),
                    Diagnostics = node.Diagnostics,
                    FullPath = childPath,
                });
            }
            else
            {
                var entry = new ScanEntry
                {
                    RelativePath = relativePath,
                    NameBytes = Encoding.UTF8.GetBytes(name),
                    NameNormalisation = NameNormalisation.Nfc,
                    Kind = ScanEntryKind.Directory,
                    Length = 0,
                    Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1ED },
                    FullPath = childPath,
                };

                yield return new ScanEvent.EnterDirectory(entry);

                if (options.Rules is null || options.Rules.MayDescend(subject))
                {
                    foreach (var scanEvent in WalkDirectory(childPath, strip, options))
                    {
                        yield return scanEvent;
                    }
                }

                yield return new ScanEvent.LeaveDirectory(entry);
            }
        }
    }

    public Stream OpenRead(ScanEntry entry)
    {
        // Keyed by FullPath — the node's own key — because a multi-root
        // adapter rewrites RelativePath with the label, exactly as the real
        // source opens by path or handle rather than by the rules subject.
        var node = _nodes[entry.FullPath];
        if (node.OpenFailure is not null)
        {
            throw node.OpenFailure;
        }

        OpenedPaths.Add(entry.FullPath);
        return new MemoryStream(node.Content, writable: false);
    }

    public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
        new MemoryStream(_nodes[entry.FullPath].AlternateStreams[streamName], writable: false);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
