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

        /// <summary>
        /// When set, reading this node's content throws once an attempt has
        /// produced this many bytes — the fault a failing medium raises
        /// part-way through a file, which a frozen buffer cannot express.
        /// Zero faults before the first byte.
        /// </summary>
        public int? FailReadAfterBytes { get; set; }

        /// <summary>The exception a mid-read fault raises; an <see cref="IOException"/> by default.</summary>
        public Exception? ReadFailure { get; set; }

        /// <summary>The first open (1-based) a mid-read fault applies to.</summary>
        public int FailFromOpen { get; set; } = 1;

        /// <summary>
        /// The content this node switches to once <see cref="MutateAfterBytes"/>
        /// bytes of an attempt have been read — the file rewritten in place
        /// under a reader already part-way through it. Applied once; the
        /// attempt in flight then reads the <em>new</em> bytes from that offset
        /// on, which is what an in-place rewrite does to an open descriptor and
        /// is precisely the torn read the publisher has to refuse.
        /// </summary>
        public byte[]? MutatedContent { get; set; }

        /// <summary>How far into an attempt <see cref="MutatedContent"/> lands.</summary>
        public int MutateAfterBytes { get; set; }

        /// <summary>The modification time the mutation stamps, so revalidation can see it.</summary>
        public ulong? MutatedModifiedAt { get; set; }

        /// <summary>
        /// Runs immediately after this node's content stream is handed out —
        /// the window in which a file is deleted or replaced while a reader
        /// holds it. Removing the node here is what makes
        /// <see cref="Revalidate"/> answer null.
        /// </summary>
        public Action<Node>? OnOpened { get; set; }

        /// <summary>How many times this node's content has been opened.</summary>
        public int Opens { get; set; }
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
        if (!_nodes.TryGetValue(entry.FullPath, out var node))
        {
            // A missing node is a missing file, not a broken test harness.
            // The bare KeyNotFoundException this used to raise is neither an
            // IOException nor an UnauthorizedAccessException, so the walker's
            // per-file catch would not have absorbed it and one vanished file
            // would have aborted the whole publication.
            throw new FileNotFoundException("The node is no longer present.", entry.FullPath);
        }

        if (node.OpenFailure is not null)
        {
            throw node.OpenFailure;
        }

        OpenedPaths.Add(entry.FullPath);
        node.Opens++;

        // A plain buffer for an unarmed node, deliberately: most of this
        // suite reads through here, and giving all of it a new stream type to
        // serve a handful of tests would be a change with no reader.
        var content = node.FailReadAfterBytes is null && node.MutatedContent is null
            ? new MemoryStream(node.Content, writable: false)
            : (Stream)new NodeContentStream(node);

        node.OnOpened?.Invoke(node);
        return content;
    }

    /// <summary>
    /// A node's content served <em>live</em> rather than frozen, so a fault or
    /// an in-place rewrite can land part-way through a read. The seam
    /// <see cref="MemoryStream"/> cannot reach.
    /// </summary>
    /// <remarks>
    /// Seekable on purpose: the publisher picks its sparse path on
    /// <c>stream.CanSeek</c>, so an unseekable stream here would quietly
    /// reroute every sparse test rather than failing one.
    /// See also <c>InterruptionHarness.FaultingStream</c>, which faults a fixed
    /// buffer and has no node to mutate.
    /// </remarks>
    private sealed class NodeContentStream(Node node) : Stream
    {
        private readonly int _length = node.Content.Length;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (node.FailReadAfterBytes is { } limit && node.Opens >= node.FailFromOpen && _position >= limit)
            {
                throw node.ReadFailure ?? new IOException("Injected fault: the source failed mid-file.");
            }

            if (node.MutatedContent is { } next && _position >= node.MutateAfterBytes)
            {
                // Once, and from this offset on. What the reader has already
                // consumed came from the old bytes and stays consumed — which
                // is the tear.
                node.Content = next;
                node.Metadata = node.Metadata with { ModifiedAt = node.MutatedModifiedAt ?? node.Metadata.ModifiedAt };
                node.MutatedContent = null;
            }

            var available = node.Content.Length - (int)_position;
            if (available <= 0)
            {
                // The file was truncated under the reader: a short read, which
                // is what the platform would give.
                return 0;
            }

            var taken = Math.Min(buffer.Length, available);
            node.Content.AsSpan((int)_position, taken).CopyTo(buffer);
            _position += taken;
            return taken;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public Stream OpenAlternateStream(ScanEntry entry, string streamName) =>
        new MemoryStream(_nodes[entry.FullPath].AlternateStreams[streamName], writable: false);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
