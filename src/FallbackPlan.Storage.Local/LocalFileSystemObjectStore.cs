using Bodu;
using System.Runtime.CompilerServices;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.Local;

/// <summary>
/// The local filesystem provider (docs/architecture/05-storage-providers.md
/// §4.1; FR-REP-002): durable file creation with an explicit flush to disk,
/// atomic temp-to-final rename where the filesystem supports it, and
/// protection against path traversal and symlink redirection when the
/// repository path is attacker-influenced.
/// </summary>
/// <remarks>
/// <para>
/// Writes spool into a <c>.fbp-tmp</c> directory at the store root. No valid
/// <see cref="ObjectKey"/> component may start with a dot, so the spool is
/// unreachable through the interface and invisible to listings.
/// </para>
/// <para>
/// After the final rename, the file's <em>contents</em> are durable (explicit
/// flush-to-disk) but the directory <em>entry</em> is best-effort: .NET
/// exposes no directory fsync. A power loss in that window can lose the
/// entry while the format's own guarantees (write intents, publication
/// ordering — Waves C and D) make the loss detectable and safe.
/// </para>
/// </remarks>
public sealed class LocalFileSystemObjectStore : IObjectStore
{
    private const string SpoolDirectoryName = ".fbp-tmp";

    private readonly string _root;
    private readonly string _spool;

    /// <summary>
    /// Creates a store rooted at <paramref name="rootPath"/>, creating the
    /// directory if it does not exist.
    /// </summary>
    public LocalFileSystemObjectStore(string rootPath)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(rootPath);

        _root = Path.GetFullPath(rootPath);
        _spool = Path.Combine(_root, SpoolDirectoryName);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public StoreCapabilities Capabilities { get; } = new()
    {
        ConditionalCreate = true,
        ConditionalReplace = false,
        RangedReads = true,
        MultipartUpload = false,
        BatchDelete = false,
        ObjectVersioning = false,
        ObjectLock = false,
        ServerSideChecksums = false,
        ListingConsistency = ListingConsistency.Strong,
        MinimumStorageDuration = null,
        ArchivalTiers = false,
        MaximumObjectSize = long.MaxValue,
        MaximumMetadataBytes = 0,
    };

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(ResolvePath(key));

        return ValueTask.FromResult(file.Exists
            ? new GetMetadataResult(new ObjectMetadata(file.Length, file.LastWriteTimeUtc))
            : GetMetadataResult.NotFound);
    }

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        FallbackPlan.Domain.Diagnostics.EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", "get"));
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(key);

        if (!File.Exists(path))
        {
            return ValueTask.FromResult(OpenReadResult.NotFound);
        }

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        if (range is not { } requested)
        {
            return ValueTask.FromResult(new OpenReadResult(stream));
        }

        if (requested.Offset + requested.Length > stream.Length)
        {
            stream.Dispose();
            return ValueTask.FromResult(OpenReadResult.RangeNotSatisfiable);
        }

        stream.Seek(requested.Offset, SeekOrigin.Begin);

        return ValueTask.FromResult(new OpenReadResult(new BoundedReadStream(stream, requested.Length)));
    }

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(openContent);
        ThrowHelper.ThrowIfNull(conditions);
        FallbackPlan.Domain.Diagnostics.EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", "put"));

        var finalPath = ResolvePath(key);

        // Store objects are immutable (specification 01 §4): an existing key
        // reports AlreadyExists — the idempotent-retry outcome — regardless of
        // conditions. Nothing here ever overwrites.
        if (File.Exists(finalPath))
        {
            return new PutResult(PutOutcome.AlreadyExists);
        }

        Directory.CreateDirectory(_spool);
        var tempPath = Path.Combine(_spool, Guid.NewGuid().ToString("n"));

        try
        {
            var content = await openContent(cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                var file = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

                    // Durable create: contents reach the platter (or its
                    // battery-backed equivalent) before the rename publishes
                    // the object.
                    file.Flush(flushToDisk: true);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // A concurrent writer won the race; ours spooled but theirs
                // published. The idempotent outcome is the same.
                File.Delete(tempPath);
                return new PutResult(PutOutcome.AlreadyExists);
            }

            return new PutResult(PutOutcome.Created);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);
        FallbackPlan.Domain.Diagnostics.EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", "list"));

        await Task.Yield();

        if (!Directory.Exists(_root))
        {
            yield break;
        }

        // Only the subtree the prefix names is walked. A prefix is a string
        // over the whole flat namespace and may end mid-component, so the
        // walk starts at the deepest directory the prefix fully names and the
        // ordinary string match still decides every candidate — narrowing
        // changes what is *visited*, never what matches. Walking the whole
        // store and filtering afterwards made one prefix listing cost the
        // entire repository, which is the difference between a per-file hint
        // lookup being cheap and being unusable.
        var searchRoot = ResolvePrefixRoot(prefix);

        if (!Directory.Exists(searchRoot))
        {
            yield break;
        }

        // The local filesystem holds everything at hand, so listing collects
        // and sorts eagerly: ordinal key order is part of the contract.
        var entries = new List<ObjectEntry>();

        foreach (var path in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

            // Spool files and anything else unreachable through the key
            // grammar are not objects.
            if (!ObjectKey.TryParse(relative, out var key))
            {
                continue;
            }

            if (!prefix.Matches(key))
            {
                continue;
            }

            if (options.ResumeAfter is { } resumeAfter &&
                string.CompareOrdinal(key.Value, resumeAfter) <= 0)
            {
                continue;
            }

            entries.Add(new ObjectEntry(key, new FileInfo(path).Length, key.Value));
        }

        entries.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(conditions);
        FallbackPlan.Domain.Diagnostics.EngineDiagnostics.StoreRequests.Add(1, new KeyValuePair<string, object?>("operation", "delete"));
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(key);

        if (!File.Exists(path))
        {
            return ValueTask.FromResult(new DeleteResult(DeleteOutcome.NotFound));
        }

        File.Delete(path);

        return ValueTask.FromResult(new DeleteResult(DeleteOutcome.Deleted));
    }

    /// <summary>
    /// The deepest directory a prefix fully names, as a filesystem path under
    /// the root; the store root when the prefix names none.
    /// </summary>
    /// <remarks>
    /// Only components the prefix <em>completes</em> — those followed by a
    /// <c>/</c> — can narrow the walk: a prefix ending mid-component names a
    /// filename fragment, not a directory, and its parent is as deep as the
    /// walk may start. Anything the prefix cannot be proven to sit under
    /// falls back to the store root, so narrowing can only ever visit fewer
    /// files, never fewer matches.
    /// </remarks>
    private string ResolvePrefixRoot(ObjectPrefix prefix)
    {
        var value = prefix.Value;
        var lastSeparator = value.LastIndexOf('/');

        if (lastSeparator <= 0)
        {
            return _root;
        }

        var directoryPart = value[..lastSeparator];

        // The grammar admits '.' and '-', so ".." is spellable and traversal
        // has to be refused rather than assumed away — the same rule
        // ResolvePath applies to keys.
        var candidate = Path.GetFullPath(Path.Combine(
            _root, directoryPart.Replace('/', Path.DirectorySeparatorChar)));

        return candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : _root;
    }

    /// <summary>
    /// Maps a key to a filesystem path with defence in depth: the grammar
    /// makes traversal unconstructible, the resolved path is verified to stay
    /// under the root, and any symlinked directory component inside the root
    /// refuses the operation — a symlink planted in an attacker-influenced
    /// repository path must not redirect writes elsewhere.
    /// </summary>
    private string ResolvePath(ObjectKey key)
    {
        if (key.Value.Length == 0)
        {
            throw new ArgumentException("The object key is empty.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(
            _root,
            key.Value.Replace('/', Path.DirectorySeparatorChar)));

        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"Key '{key}' resolves outside the store root.");
        }

        // Walk every ancestor up to the root, not stopping at levels that do
        // not exist yet: the symlinked component may sit above a directory the
        // put would otherwise create through it.
        var directory = Path.GetDirectoryName(path);
        while (directory is not null && directory.Length > _root.Length)
        {
            if (Directory.Exists(directory) &&
                Directory.ResolveLinkTarget(directory, returnFinalTarget: false) is not null)
            {
                throw new IOException($"Refusing to traverse the symlinked directory '{directory}' inside the store root.");
            }

            directory = Path.GetDirectoryName(directory);
        }

        return path;
    }

    /// <summary>
    /// A read-only view over an underlying stream, bounded to a byte budget —
    /// the shape of a ranged read.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        internal BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)],
                cancellationToken).ConfigureAwait(false);
            _remaining -= read;

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
