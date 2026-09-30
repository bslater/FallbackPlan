using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using System.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository.Catalogue;

/// <summary>One snapshot as the catalogue projects it (schema v2).</summary>
public sealed record CatalogueSnapshot(
    ReadOnlyMemory<byte> SnapshotId,
    ReadOnlyMemory<byte> DeviceId,
    ReadOnlyMemory<byte> BackupSetId,
    ObjectId ObjectId,
    ObjectId RootTree,
    ulong PublicationGeneration,
    byte CaptureStatus,
    int SignatureState,
    ulong CapturedAt,
    byte ConsistencyMethod = 1,
    long? ObservedClockSkewMs = null);

/// <summary>
/// One path within a snapshot (schema v2 <c>tree_entries</c>), joined with
/// the file-version columns the NFR-PERF-003 short-circuit compares. The
/// identity columns are scan-time local facts — never durable in the
/// repository — so they are null in a rebuilt catalogue, and a null
/// identity disables the short-circuit rather than weakening it.
/// </summary>
public sealed record CatalogueTreeEntry(
    string Path,
    EntryKind EntryKind,
    ObjectId ObjectId,
    ulong? LogicalLength,
    ulong? ModifiedAt,
    ulong? IdentityDevice,
    ulong? IdentityFileId,
    bool HasAlternateStreams = false,
    ReadOnlyMemory<byte>? MetadataDigest = null);

/// <summary>
/// What the reuse decision needs to know about an object the index already
/// locates ([ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)):
/// who wrote the winning entry, and whether this device has already confirmed
/// its content.
/// </summary>
public sealed record ReuseCandidate(WriterId WriterId, bool Verified);

/// <summary>A resolved physical location: everything a targeted read needs to open one blob and one record.</summary>
/// <remarks>
/// <see cref="StoreBlobKey"/> comes from the blob row rather than the location
/// row and is null when no blob row exists — an index delta names locations
/// whether or not this catalogue has seen the blob that holds them, which is
/// the ordinary state of a rebuilt catalogue. A reader that needs to address
/// the blob derives its store key from <see cref="BlobId"/> instead.
/// </remarks>
public sealed record ResolvedLocation(
    BlobId BlobId,
    StoreBlobKey? StoreBlobKey,
    ulong PhysicalOffset,
    uint StoredLength,
    ushort CompressionProfileValue,
    ushort EncryptionProfileValue,
    ulong Generation,
    WriterId WriterId,
    ulong Sequence);

/// <summary>
/// What damaged blobs are needed by, as far as the catalogue can trace it
/// (FR-VER-005, specification 04 §7): the snapshots a restore of which would
/// meet the damage, and the paths in them.
/// </summary>
/// <param name="Snapshots">
/// Every snapshot that needs one of the damaged objects — for a file's
/// content or version, or for its own structure — by lowercase hex identity.
/// </param>
/// <param name="Files">Distinct paths whose version needs one of them, across those snapshots.</param>
/// <param name="FileSample">The first of those paths in ordinal order, as many as were asked for.</param>
/// <param name="Structures">Snapshots whose own records — manifest, policy, error manifest, a tree — one of them holds.</param>
/// <param name="Untraced">
/// Damaged objects, and damaged blobs, the catalogue cannot trace to anything
/// that needs them. Where any are, the damage may reach any snapshot, and a
/// caller must count every one as reached.
/// </param>
public sealed record DamageReach(
    IReadOnlySet<string> Snapshots, int Files, IReadOnlyList<string> FileSample, int Structures, int Untraced)
{
    /// <summary>Nothing damaged, and so nothing reached.</summary>
    public static DamageReach None { get; } = new(new HashSet<string>(StringComparer.Ordinal), 0, [], 0, 0);

    /// <summary>Whether every damaged object was traced, so that <see cref="Snapshots"/> is all that needs them.</summary>
    public bool Complete => Untraced == 0;
}

/// <summary>
/// The local catalogue (architecture 02 §7; FR-MAN-002, FR-MAN-005;
/// NFR-PERF-004, NFR-PERF-010): a disposable SQLite cache of index and
/// manifest state. It is never authoritative — a schema or repository
/// mismatch drops and rebuilds, and nothing in it is required for
/// correctness, only for speed. The location resolver implements the exact
/// 07 §3 precedence order in SQL and is parity-tested against
/// <see cref="IndexPrecedence"/>.
/// </summary>
public sealed class Catalogue : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _log;

    private Catalogue(SqliteConnection connection, ILogger? logger)
    {
        _connection = connection;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Opens (or creates) the catalogue at <paramref name="path"/> for
    /// <paramref name="repositoryId"/>. Any mismatch — schema version or
    /// repository identity — deletes and recreates: it is a cache
    /// (FR-MAN-002).
    /// </summary>
    public static Catalogue Open(string path, RepositoryId repositoryId, ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        var connection = Connect(path);
        var disposition = "reused";

        if (!IsCompatible(connection, repositoryId))
        {
            connection.Dispose();
            File.Delete(path);
            connection = Connect(path);
            disposition = "discarded and recreated — schema version or repository identity did not match";
        }

        if (!HasSchema(connection))
        {
            disposition = disposition == "reused" ? "created" : disposition;

            using var create = connection.CreateCommand();
            create.CommandText = CatalogueSchema.Ddl;
            create.ExecuteNonQuery();

            // Created empty, and so marked: a new file holds nothing whatever
            // the repository holds, and it says so until a rebuild that read
            // everything clears it (FR-MAN-002).
            using var stamp = connection.CreateCommand();
            stamp.CommandText = """
                INSERT INTO catalogue_info (key, value) VALUES
                ('schema_version', $version),
                ('repository_id', $repository),
                ('source', 'live'),
                ('rebuild_pending', '1');
                """;
            stamp.Parameters.AddWithValue("$version", CatalogueSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stamp.Parameters.AddWithValue("$repository", Convert.ToHexStringLower(repositoryId.ToArray()));
            stamp.ExecuteNonQuery();
        }

        Log.CatalogueOpened(logger ?? NullLogger.Instance, repositoryId, disposition);

        return new Catalogue(connection, logger);
    }

    /// <summary>
    /// Whether this catalogue was created — new, or in place of one another
    /// schema or repository wrote — and has not since been rebuilt from its
    /// repository by a rebuild that read every record it needed (FR-MAN-002).
    /// </summary>
    /// <remarks>
    /// Held in the file rather than the process, so a rebuild cut short is
    /// tried again instead of being taken for one that finished. A catalogue
    /// that needs rebuilding answers as though its repository had no history,
    /// which is what a reader must not be left to believe.
    /// </remarks>
    public bool NeedsRebuild
    {
        get
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM catalogue_info WHERE key = 'rebuild_pending');";
            return (long)command.ExecuteScalar()! > 0;
        }
    }

    /// <summary>Records that a rebuild read every record it needed into this catalogue.</summary>
    public void MarkRebuilt()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM catalogue_info WHERE key = 'rebuild_pending';";
        command.ExecuteNonQuery();
    }

    /// <summary>Marks how this catalogue was produced: live, checkpoint-rebuild, or forensic-rebuild.</summary>
    public void SetSource(string source)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE catalogue_info SET value = $value WHERE key = 'source';";
        command.Parameters.AddWithValue("$value", source);
        command.ExecuteNonQuery();
    }

    /// <summary>Records one blob's physical facts, including the digest's catalogue-domain home (Q16).</summary>
    public void RecordBlob(
        BlobId blobId,
        StoreBlobKey storeBlobKey,
        BlobClass blobClass,
        KeyGeneration keyGeneration,
        int recordCount,
        long length,
        ReadOnlySpan<byte> digest)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO blobs (blob_id, store_blob_key, blob_class, key_generation, record_count, length, digest)
            VALUES ($id, $key, $class, $generation, $records, $length, $digest)
            ON CONFLICT (blob_id) DO UPDATE SET digest = excluded.digest;
            """;
        command.Parameters.AddWithValue("$id", blobId.ToArray());
        command.Parameters.AddWithValue("$key", storeBlobKey.ToArray());
        command.Parameters.AddWithValue("$class", (int)blobClass);
        command.Parameters.AddWithValue("$generation", keyGeneration.Value);
        command.Parameters.AddWithValue("$records", recordCount);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$digest", digest.IsEmpty ? DBNull.Value : digest.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Marks a blob's lifecycle state (1 live, 2 tombstoned, 3 deleted) for precedence rule 3.</summary>
    /// <remarks>
    /// A blob no <see cref="RecordBlob"/> has described gets a placeholder
    /// row whose store key is its own id — the column is not null and
    /// unique, and the id is both. Readers treat that shape as "no store key
    /// recorded" and derive one (<see cref="QueryWinner"/>), so a placeholder
    /// never masquerades as a physical fact.
    /// </remarks>
    public void SetBlobState(BlobId blobId, BlobState state)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO blobs (blob_id, store_blob_key, blob_class, key_generation, record_count, length, state)
            VALUES ($id, $id, 0, 0, 0, 0, $state)
            ON CONFLICT (blob_id) DO UPDATE SET state = excluded.state;
            """;
        command.Parameters.AddWithValue("$id", blobId.ToArray());
        command.Parameters.AddWithValue("$state", (int)state);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies one delta idempotently: the ledger row makes re-application a
    /// no-op, so replaying the same delta after a crash converges (07 §6).
    /// </summary>
    public void ApplyDelta(DeltaId deltaId, IndexDelta delta)
    {
        ThrowHelper.ThrowIfNull(delta);

        using var transaction = _connection.BeginTransaction();

        using (var ledger = _connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                INSERT OR IGNORE INTO index_deltas (delta_id, writer_id, sequence, generation, predecessor_delta_id, is_void)
                VALUES ($id, $writer, $sequence, $generation, $predecessor, $void);
                """;
            ledger.Parameters.AddWithValue("$id", deltaId.ToArray());
            ledger.Parameters.AddWithValue("$writer", delta.WriterId.ToArray());
            ledger.Parameters.AddWithValue("$sequence", (long)delta.Sequence);
            ledger.Parameters.AddWithValue("$generation", (long)delta.Generation);
            ledger.Parameters.AddWithValue("$predecessor", delta.PredecessorDeltaId is { } p ? p.ToArray() : DBNull.Value);
            ledger.Parameters.AddWithValue("$void", delta.IsVoid ? 1 : 0);

            if (ledger.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return; // already applied
            }
        }

        foreach (var entry in delta.Entries)
        {
            InsertLocation(transaction, entry, delta.Generation, delta.WriterId, delta.Sequence);
        }

        // The signed blob digests ride the delta (07 §2.2) so that a catalogue
        // rebuilt from the index plane alone holds them: they are what lets a
        // verifier prove a sealed blob it cannot open at a destination. A
        // delta that carries none records nothing about its blobs, and a row
        // RecordBlob already described keeps every physical fact it has.
        if (delta.CoveredBlobDigests.Count > 0 && delta.CoveredBlobDigests.Count == delta.CoveredBlobIds.Count)
        {
            for (var i = 0; i < delta.CoveredBlobIds.Count; i++)
            {
                UpsertDigest(transaction, delta.CoveredBlobIds[i], delta.CoveredBlobDigests[i]);
            }
        }

        // And the Merkle commitment beside it (07 §2.3), on the same terms:
        // a delta that carries none records nothing, and never clears a root
        // an earlier delta established.
        if (delta.CoveredBlobMerkleRoots.Count > 0
            && delta.CoveredBlobMerkleRoots.Count == delta.CoveredBlobIds.Count)
        {
            for (var i = 0; i < delta.CoveredBlobIds.Count; i++)
            {
                UpsertMerkleRoot(transaction, delta.CoveredBlobIds[i], delta.CoveredBlobMerkleRoots[i]);
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// The signed digest of a blob's sealed bytes, or <see langword="null"/>
    /// when none is on record. It reached the catalogue either from a delta
    /// this reader authenticated or from this writer's own seal — never from
    /// a destination — so it is something to check a replica's bytes against.
    /// </summary>
    public ReadOnlyMemory<byte>? SignedDigestOf(BlobId blobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT digest FROM blobs WHERE blob_id = $id;";
        command.Parameters.AddWithValue("$id", blobId.ToArray());

        // Spelled out rather than as a conditional: a null byte[] converts
        // to an EMPTY memory, not to no memory, and "no digest on record"
        // must never read as "a digest of nothing".
        if (command.ExecuteScalar() is byte[] digest)
        {
            return digest;
        }

        return null;
    }

    /// <summary>
    /// The signed Merkle commitment over a blob's sealed bytes, or
    /// <see langword="null"/> when none is on record — which is every blob
    /// of a repository below format 3, and every blob whose delta a reader
    /// applied before this column existed.
    /// </summary>
    /// <remarks>
    /// Its provenance is the digest's: a delta this reader authenticated or
    /// this writer's own seal, never a destination. That is what makes it
    /// something to challenge a replica against rather than something to
    /// compare a replica with itself.
    /// </remarks>
    public ReadOnlyMemory<byte>? SignedMerkleRootOf(BlobId blobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT merkle_root FROM blobs WHERE blob_id = $id;";
        command.Parameters.AddWithValue("$id", blobId.ToArray());

        // Spelled out for the reason SignedDigestOf is: a null byte[] would
        // convert to an empty memory, and "no root on record" must never
        // read as "a root of nothing".
        if (command.ExecuteScalar() is byte[] root)
        {
            return root;
        }

        return null;
    }

    private static void UpsertDigest(SqliteTransaction transaction, BlobId blobId, ReadOnlyMemory<byte> digest)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO blobs (blob_id, store_blob_key, blob_class, key_generation, record_count, length, digest)
            VALUES ($id, $id, 0, 0, 0, 0, $digest)
            ON CONFLICT (blob_id) DO UPDATE SET digest = excluded.digest;
            """;
        command.Parameters.AddWithValue("$id", blobId.ToArray());
        command.Parameters.AddWithValue("$digest", digest.ToArray());
        command.ExecuteNonQuery();
    }

    private static void UpsertMerkleRoot(SqliteTransaction transaction, BlobId blobId, ReadOnlyMemory<byte> root)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;

        // Its own statement rather than a column on the digest's: a delta
        // that carries digests and no roots must leave a root already on
        // record alone, and one UPDATE over both columns would erase it.
        command.CommandText = """
            INSERT INTO blobs (blob_id, store_blob_key, blob_class, key_generation, record_count, length, merkle_root)
            VALUES ($id, $id, 0, 0, 0, 0, $root)
            ON CONFLICT (blob_id) DO UPDATE SET merkle_root = excluded.merkle_root;
            """;
        command.Parameters.AddWithValue("$id", blobId.ToArray());
        command.Parameters.AddWithValue("$root", root.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Applies one checkpoint idempotently, entries carrying the checkpoint's provenance.</summary>
    public void ApplyCheckpoint(CheckpointId checkpointId, Checkpoint checkpoint)
    {
        ThrowHelper.ThrowIfNull(checkpoint);

        using var transaction = _connection.BeginTransaction();

        using (var ledger = _connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                INSERT OR IGNORE INTO checkpoints (checkpoint_id, generation, writer_id, predecessor_checkpoint_id)
                VALUES ($id, $generation, $writer, $predecessor);
                """;
            ledger.Parameters.AddWithValue("$id", checkpointId.ToArray());
            ledger.Parameters.AddWithValue("$generation", (long)checkpoint.Generation);
            ledger.Parameters.AddWithValue("$writer", checkpoint.WriterId.ToArray());
            ledger.Parameters.AddWithValue("$predecessor", checkpoint.PredecessorCheckpointId is { } p ? p.ToArray() : DBNull.Value);

            if (ledger.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return;
            }
        }

        foreach (var watermark in checkpoint.WriterWatermarks)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO checkpoint_watermarks (checkpoint_id, writer_id, highest_sequence)
                VALUES ($id, $writer, $sequence);
                """;
            command.Parameters.AddWithValue("$id", checkpointId.ToArray());
            command.Parameters.AddWithValue("$writer", watermark.WriterId.ToArray());
            command.Parameters.AddWithValue("$sequence", (long)watermark.HighestSequence);
            command.ExecuteNonQuery();
        }

        var ownWatermark = checkpoint.WriterWatermarks
            .FirstOrDefault(mark => mark.WriterId == checkpoint.WriterId)?.HighestSequence ?? 0;

        foreach (var entry in checkpoint.Entries)
        {
            InsertLocation(transaction, entry, checkpoint.Generation, checkpoint.WriterId, ownWatermark);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Records a file-version manifest's projection. The optional
    /// modification time and identity are the NFR-PERF-003 short-circuit's
    /// comparison key; identity is catalogue-domain only (02 §2) and absent
    /// after any rebuild.
    /// </summary>
    public void RecordFileVersion(
        ObjectId objectId,
        ReadOnlySpan<byte> name,
        EntryKind entryKind,
        ulong logicalLength,
        ReadOnlySpan<byte> wholeFileHash,
        ObjectId? parentVersion,
        int segmentCount,
        ulong? modifiedAt = null,
        ulong? identityDevice = null,
        ulong? identityFileId = null,
        bool hasAlternateStreams = false,
        ReadOnlyMemory<byte>? metadataDigest = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO file_versions
                (object_id, name, entry_kind, logical_length, whole_file_hash, parent_version, segment_count,
                 modified_at, identity_device, identity_file_id, has_alternate_streams, metadata_digest)
            VALUES ($id, $name, $kind, $length, $hash, $parent, $segments, $modified, $device, $fileid, $streams,
                    $metadata);
            """;
        command.Parameters.AddWithValue("$id", objectId.ToArray());
        command.Parameters.AddWithValue("$name", name.ToArray());
        command.Parameters.AddWithValue("$kind", (int)entryKind);
        command.Parameters.AddWithValue("$length", (long)logicalLength);
        command.Parameters.AddWithValue("$hash", wholeFileHash.ToArray());
        command.Parameters.AddWithValue("$parent", parentVersion is { } p ? p.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$segments", segmentCount);
        command.Parameters.AddWithValue("$modified", modifiedAt is { } m ? (long)m : DBNull.Value);
        command.Parameters.AddWithValue("$device", identityDevice is { } d ? unchecked((long)d) : DBNull.Value);
        command.Parameters.AddWithValue("$fileid", identityFileId is { } f ? unchecked((long)f) : DBNull.Value);
        command.Parameters.AddWithValue("$streams", hasAlternateStreams ? 1 : 0);
        command.Parameters.AddWithValue(
            "$metadata", metadataDigest is { } digest ? digest.ToArray() : (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The catalogue's path index key: the invariant lowercase form of the
    /// NFC-normalised path — the same case regime rules-v1 matching uses
    /// (ADR-0026 §Decision 8). Applied for indexing only; stored paths keep
    /// their original form.
    /// </summary>
    public static string Casefold(string path)
    {
        ThrowHelper.ThrowIfNull(path);
        return path.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Records one path of a snapshot's tree (schema v2 <c>tree_entries</c>).</summary>
    public void RecordTreeEntry(
        ReadOnlySpan<byte> snapshotId,
        string path,
        EntryKind entryKind,
        ObjectId objectId)
    {
        ThrowHelper.ThrowIfNullOrEmpty(path);

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : path[..slash];

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO tree_entries (snapshot_id, path, parent, path_casefold, entry_kind, object_id)
            VALUES ($snapshot, $path, $parent, $casefold, $kind, $object);
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$casefold", Casefold(path));
        command.Parameters.AddWithValue("$kind", (int)entryKind);
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records the content objects <paramref name="versionId"/> needs — its
    /// segments and its alternate streams' content — so that damage to one
    /// can be traced to the version, and through it to the snapshots and
    /// paths holding it (FR-VER-005).
    /// </summary>
    public void RecordVersionContents(ObjectId versionId, IEnumerable<ObjectId> contents)
    {
        ThrowHelper.ThrowIfNull(contents);

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO version_contents (object_id, version_id) VALUES ($object, $version);";
        var content = command.Parameters.Add("$object", SqliteType.Blob);
        command.Parameters.AddWithValue("$version", versionId.ToArray());
        foreach (var objectId in contents)
        {
            content.Value = objectId.ToArray();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Records one of the records <paramref name="snapshotId"/> is made of
    /// besides its files' own: its manifest, its policy or error manifest, or
    /// a tree manifest of its structure.
    /// </summary>
    public void RecordSnapshotStructure(ReadOnlySpan<byte> snapshotId, ObjectId objectId) =>
        RecordSnapshotStructure(snapshotId, [objectId]);

    /// <summary>Records records <paramref name="snapshotId"/> is made of, as <see cref="RecordSnapshotStructure(ReadOnlySpan{byte}, ObjectId)"/> does one.</summary>
    public void RecordSnapshotStructure(ReadOnlySpan<byte> snapshotId, IEnumerable<ObjectId> objectIds)
    {
        ThrowHelper.ThrowIfNull(objectIds);

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO snapshot_structure (object_id, snapshot_id) VALUES ($object, $snapshot);";
        var record = command.Parameters.Add("$object", SqliteType.Blob);
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        foreach (var objectId in objectIds)
        {
            record.Value = objectId.ToArray();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Every blob some object is located in, once each — what a store key names is matched against.</summary>
    public IReadOnlyList<BlobId> LocatedBlobs()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT blob_id FROM object_locations;";

        var blobs = new List<BlobId>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            blobs.Add(BlobId.FromBytes((byte[])reader.GetValue(0)));
        }

        return blobs;
    }

    /// <summary>
    /// What <paramref name="blobs"/> reach, taken as damaged: the objects a
    /// read would take from them, the file versions that need those objects,
    /// and so the snapshots and paths holding those versions (FR-VER-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An object is taken from a damaged blob only when its winning location
    /// is there (07 §3): a copy compaction has superseded is nothing a read
    /// would reach. An object nothing traces — no version needs it, it is no
    /// version, and no snapshot is built of it — is counted rather than
    /// dropped, because a caller that cannot place damage must not report the
    /// snapshots it could not rule out as untouched.
    /// </para>
    /// <para>
    /// The paths come from one pass over the snapshots' listings, which
    /// <c>tree_entries</c> keeps by snapshot and path and not by version: the
    /// cost of a listing's count, paid only when damage is outstanding.
    /// </para>
    /// </remarks>
    /// <param name="blobs">The damaged blobs.</param>
    /// <param name="sampleLimit">How many paths to name; the count covers them all.</param>
    public DamageReach ReachOf(IReadOnlyCollection<BlobId> blobs, int sampleLimit)
    {
        ThrowHelper.ThrowIfNull(blobs);
        ThrowHelper.ThrowIfLessThan(sampleLimit, 0);

        var damaged = blobs.ToHashSet();
        var reached = new HashSet<ObjectId>();
        foreach (var blob in damaged)
        {
            foreach (var objectId in ObjectsLocatedIn(blob))
            {
                if (QueryWinner(objectId, excludeDeleted: true) is { } winner && damaged.Contains(winner.BlobId))
                {
                    reached.Add(objectId);
                }
            }
        }

        var versions = new HashSet<ObjectId>();
        var structures = new HashSet<string>(StringComparer.Ordinal);
        var untraced = 0;
        foreach (var objectId in reached)
        {
            var traced = false;
            foreach (var version in VersionsNeeding(objectId))
            {
                versions.Add(version);
                traced = true;
            }

            if (IsFileVersion(objectId))
            {
                versions.Add(objectId);
                traced = true;
            }

            foreach (var snapshot in SnapshotsBuiltOf(objectId))
            {
                structures.Add(snapshot);
                traced = true;
            }

            if (!traced)
            {
                untraced++;
            }
        }

        var snapshots = new HashSet<string>(structures, StringComparer.Ordinal);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (snapshot, path) in PathsHolding(versions))
        {
            snapshots.Add(snapshot);
            paths.Add(path);
        }

        return new DamageReach(snapshots, paths.Count, [.. paths.Take(sampleLimit)], structures.Count, untraced);
    }

    private List<ObjectId> ObjectsLocatedIn(BlobId blob)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT object_id FROM object_locations WHERE blob_id = $blob;";
        command.Parameters.AddWithValue("$blob", blob.ToArray());

        var objects = new List<ObjectId>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            objects.Add(ObjectId.FromBytes((byte[])reader.GetValue(0)));
        }

        return objects;
    }

    private List<ObjectId> VersionsNeeding(ObjectId objectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version_id FROM version_contents WHERE object_id = $object;";
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        var versions = new List<ObjectId>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(ObjectId.FromBytes((byte[])reader.GetValue(0)));
        }

        return versions;
    }

    private bool IsFileVersion(ObjectId objectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM file_versions WHERE object_id = $object);";
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        return (long)command.ExecuteScalar()! > 0;
    }

    private List<string> SnapshotsBuiltOf(ObjectId objectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT snapshot_id FROM snapshot_structure WHERE object_id = $object;";
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        var snapshots = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(Convert.ToHexStringLower((byte[])reader.GetValue(0)));
        }

        return snapshots;
    }

    /// <summary>Every (snapshot, path) whose file entry is one of <paramref name="versions"/>.</summary>
    private List<(string Snapshot, string Path)> PathsHolding(HashSet<ObjectId> versions)
    {
        var held = new List<(string, string)>();
        if (versions.Count == 0)
        {
            return held;
        }

        // Deferred: the only table written is the connection's own temporary
        // one, so the catalogue's write lock is never taken. An immediate
        // transaction would stall a status read behind a capture's writes.
        using var transaction = _connection.BeginTransaction(deferred: true);
        using (var create = _connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS reach_versions (id BLOB PRIMARY KEY) WITHOUT ROWID;
                DELETE FROM reach_versions;
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO reach_versions (id) VALUES ($id);";
            var id = insert.Parameters.Add("$id", SqliteType.Blob);
            foreach (var version in versions)
            {
                id.Value = version.ToArray();
                insert.ExecuteNonQuery();
            }
        }

        using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = $"""
                SELECT t.snapshot_id, t.path FROM tree_entries t
                WHERE t.entry_kind <> {(int)EntryKind.DirectoryPlaceholder}
                  AND t.object_id IN (SELECT id FROM reach_versions);
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                held.Add((Convert.ToHexStringLower((byte[])reader.GetValue(0)), reader.GetString(1)));
            }
        }

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM reach_versions;";
            clear.ExecuteNonQuery();
        }

        transaction.Commit();
        return held;
    }

    /// <summary>Every known snapshot, newest capture first.</summary>
    public IReadOnlyList<CatalogueSnapshot> EnumerateSnapshots()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, device_id, backup_set_id, object_id, root_tree,
                   publication_generation, capture_status, signature_state, captured_at,
                   consistency_method, observed_clock_skew_ms
            FROM snapshots
            ORDER BY captured_at DESC, snapshot_id;
            """;

        var snapshots = new List<CatalogueSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(new CatalogueSnapshot(
                (byte[])reader.GetValue(0),
                (byte[])reader.GetValue(1),
                (byte[])reader.GetValue(2),
                ObjectId.FromBytes((byte[])reader.GetValue(3)),
                ObjectId.FromBytes((byte[])reader.GetValue(4)),
                (ulong)reader.GetInt64(5),
                (byte)reader.GetInt64(6),
                (int)reader.GetInt64(7),
                (ulong)reader.GetInt64(8),
                (byte)reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10)));
        }

        return snapshots;
    }

    /// <summary>One snapshot's capture time, or <see langword="null"/> when it is unknown here.</summary>
    /// <param name="snapshotId">The snapshot, 16 bytes.</param>
    /// <remarks>
    /// Snapshot rows survive a catalogue rebuild — the projector reads the
    /// standalone snapshot objects — so this answers even when file-version
    /// identities do not.
    /// </remarks>
    public ulong? LookupSnapshotCaptureTime(ReadOnlySpan<byte> snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT captured_at FROM snapshots WHERE snapshot_id = $snapshot LIMIT 1;";
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (ulong)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether this snapshot's file versions carry the scan-time identity the
    /// incremental path compares.
    /// </summary>
    /// <param name="snapshotId">The snapshot, 16 bytes.</param>
    /// <remarks>
    /// The gate on consulting the durable source-identity hints (06 §11).
    /// Identity is scan-time local fact and is not durable (02 §2), so a
    /// rebuilt catalogue has none and the hints are the only remaining answer;
    /// a warm one has them all and answering from the store would be a
    /// round trip to learn what is already in hand.
    /// </remarks>
    public bool HasIdentities(ReadOnlySpan<byte> snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM tree_entries t
            JOIN file_versions f ON f.object_id = t.object_id
            WHERE t.snapshot_id = $snapshot AND f.identity_file_id IS NOT NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());

        return command.ExecuteScalar() is not (null or DBNull);
    }

    /// <summary>
    /// Resolves one path within a snapshot — the NFR-PERF-004 lookup.
    /// Case-insensitive resolution folds through the ADR-0026 §Decision 8
    /// key; an exact match always wins over a folded one.
    /// </summary>
    /// <summary>
    /// Finds a snapshot's version of a file by the source's **stable
    /// identity** rather than by its path.
    /// </summary>
    /// <param name="snapshotId">The snapshot to look in.</param>
    /// <param name="device">The source device number or volume identifier.</param>
    /// <param name="fileId">The inode or file identifier.</param>
    /// <returns>The prior entry, or <see langword="null"/> when this snapshot has no file with that identity.</returns>
    /// <remarks>
    /// <para>
    /// This is what makes a rename or a move recognisable as the same file
    /// rather than a delete plus a create (architecture 06 §1). Keyed on path,
    /// a moved file misses entirely, and the engine re-reads and re-hashes
    /// every byte of a file whose content did not change — and writes a
    /// version with no ancestor, permanently severing the history a user
    /// renamed rather than replaced.
    /// </para>
    /// <para>
    /// Identity alone is not sufficient to reuse content, and this does not
    /// claim it is: an inode is reused after its file is deleted. The caller
    /// still checks size and modification time, exactly as it does for a
    /// path match.
    /// </para>
    /// </remarks>
    public CatalogueTreeEntry? LookupIdentity(ReadOnlySpan<byte> snapshotId, ulong device, ulong fileId)
    {
        var started = Stopwatch.GetTimestamp();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
            FROM file_versions f
            JOIN tree_entries t ON t.object_id = f.object_id AND t.snapshot_id = $snapshot
            WHERE f.identity_device = $device AND f.identity_file_id = $fileId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$device", (long)device);
        command.Parameters.AddWithValue("$fileId", (long)fileId);

        using var reader = command.ExecuteReader();
        var entry = reader.Read() ? ReadTreeEntry(reader) : null;
        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return entry;
    }

    public CatalogueTreeEntry? LookupPath(ReadOnlySpan<byte> snapshotId, string path, bool caseInsensitive = false)
    {
        ThrowHelper.ThrowIfNullOrEmpty(path);
        var started = Stopwatch.GetTimestamp();

        using var command = _connection.CreateCommand();
        command.CommandText = caseInsensitive
            ? """
              SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
              FROM tree_entries t
              LEFT JOIN file_versions f ON f.object_id = t.object_id
              WHERE t.snapshot_id = $snapshot AND t.path_casefold = $casefold
              ORDER BY (t.path = $path) DESC, t.path
              LIMIT 1;
              """
            : """
              SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
              FROM tree_entries t
              LEFT JOIN file_versions f ON f.object_id = t.object_id
              WHERE t.snapshot_id = $snapshot AND t.path = $path;
              """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$path", path);
        if (caseInsensitive)
        {
            command.Parameters.AddWithValue("$casefold", Casefold(path));
        }

        using var reader = command.ExecuteReader();
        var entry = reader.Read() ? ReadTreeEntry(reader) : null;
        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return entry;
    }

    /// <summary>
    /// Lists a directory's immediate children within a snapshot, in path
    /// order. The root is the empty string.
    /// </summary>
    public IReadOnlyList<CatalogueTreeEntry> ListDirectory(ReadOnlySpan<byte> snapshotId, string parentPath)
    {
        ThrowHelper.ThrowIfNull(parentPath);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
            FROM tree_entries t
            LEFT JOIN file_versions f ON f.object_id = t.object_id
            WHERE t.snapshot_id = $snapshot AND t.parent = $parent
            ORDER BY t.path;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$parent", parentPath);

        var entries = new List<CatalogueTreeEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadTreeEntry(reader));
        }

        return entries;
    }

    /// <summary>
    /// Every non-directory entry of one snapshot, in path order — the
    /// baseline a source comparison walks against (FR-SVC-009).
    /// </summary>
    public IReadOnlyList<CatalogueTreeEntry> EnumerateLeaves(ReadOnlySpan<byte> snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
            FROM tree_entries t
            LEFT JOIN file_versions f ON f.object_id = t.object_id
            WHERE t.snapshot_id = $snapshot AND t.entry_kind <> $directory
            ORDER BY t.path;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$directory", (long)EntryKind.DirectoryPlaceholder);

        var entries = new List<CatalogueTreeEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadTreeEntry(reader));
        }

        return entries;
    }

    /// <summary>How many non-directory entries one snapshot holds.</summary>
    public long CountFiles(ReadOnlySpan<byte> snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM tree_entries
            WHERE snapshot_id = $snapshot AND entry_kind <> $directory;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$directory", (long)EntryKind.DirectoryPlaceholder);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static CatalogueTreeEntry ReadTreeEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        (EntryKind)reader.GetInt64(1),
        ObjectId.FromBytes((byte[])reader.GetValue(2)),
        reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3),
        reader.IsDBNull(4) ? null : (ulong)reader.GetInt64(4),
        reader.IsDBNull(5) ? null : unchecked((ulong)reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : unchecked((ulong)reader.GetInt64(6)),
        !reader.IsDBNull(7) && reader.GetInt64(7) > 0,
        reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8));

    /// <summary>Records a content-to-object dedup mapping — catalogue-domain only, never durable in the repository (02 §2).</summary>
    public void RecordSegmentDedup(ContentId contentId, ObjectId objectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO segment_dedup (content_id, object_id) VALUES ($content, $object);
            """;
        command.Parameters.AddWithValue("$content", contentId.ToArray());
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Records a snapshot's projection with its signature verdict (1 verified, 2 failed, 3 unverified).</summary>
    public void RecordSnapshot(
        ReadOnlySpan<byte> snapshotId,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> backupSetId,
        ObjectId objectId,
        ObjectId rootTree,
        ulong publicationGeneration,
        byte captureStatus,
        int signatureState,
        ulong capturedAt = 0,
        byte consistencyMethod = 1,
        long? observedClockSkewMs = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO snapshots
                (snapshot_id, device_id, backup_set_id, object_id, root_tree, publication_generation, capture_status, signature_state, captured_at, consistency_method, observed_clock_skew_ms)
            VALUES ($id, $device, $set, $object, $root, $generation, $status, $signature, $captured, $consistency, $skew);
            """;
        command.Parameters.AddWithValue("$id", snapshotId.ToArray());
        command.Parameters.AddWithValue("$device", deviceId.ToArray());
        command.Parameters.AddWithValue("$set", backupSetId.ToArray());
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.Parameters.AddWithValue("$root", rootTree.ToArray());
        command.Parameters.AddWithValue("$generation", (long)publicationGeneration);
        command.Parameters.AddWithValue("$status", captureStatus);
        command.Parameters.AddWithValue("$signature", signatureState);
        command.Parameters.AddWithValue("$captured", (long)capturedAt);
        command.Parameters.AddWithValue("$consistency", consistencyMethod);
        command.Parameters.AddWithValue("$skew", observedClockSkewMs is { } skew ? skew : DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Appends a damage finding (FR-MAN-011).</summary>
    public void RecordFinding(DamageFinding finding, ObjectId? objectId = null, BlobId? blobId = null)
    {
        ThrowHelper.ThrowIfNull(finding);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO damage_findings (kind, object_id, blob_id, detail) VALUES ($kind, $object, $blob, $detail);
            """;
        command.Parameters.AddWithValue("$kind", (int)finding.Kind);
        command.Parameters.AddWithValue("$object", objectId is { } o ? o.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$blob", blobId is { } b ? b.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$detail", finding.Detail);
        command.ExecuteNonQuery();

        // Every finding funnels through here — live projection, checkpoint
        // rebuild and the forensic walk alike — so one call site covers all
        // three rather than three that can drift apart.
        Log.DamageFound(_log, finding.Kind, finding.Detail);
    }

    /// <summary>Every recorded finding, in insertion order.</summary>
    public IReadOnlyList<DamageFinding> Findings()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT kind, detail FROM damage_findings ORDER BY id;";

        var findings = new List<DamageFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            findings.Add(new DamageFinding((DamageKind)reader.GetInt32(0), reader.GetString(1)));
        }

        return findings;
    }

    /// <summary>
    /// Resolves the winning location for an object — the NFR-PERF-004/010
    /// lookup path, honouring 07 §3 in SQL: highest generation, then writer
    /// bytes, then sequence, with deleted-blob winners excluded and reported
    /// (rule 3).
    /// </summary>
    public ResolvedLocation? ResolveLocation(ObjectId objectId)
    {
        // The raw winner including deleted blobs, to detect the rule-3 case.
        var raw = QueryWinner(objectId, excludeDeleted: false);
        if (raw is null)
        {
            return null;
        }

        var live = QueryWinner(objectId, excludeDeleted: true);

        if (live is null || !raw.BlobId.Equals(live.BlobId))
        {
            RecordFinding(new DamageFinding(
                DamageKind.MissingBlob,
                $"The winning index entry for object {objectId} names deleted blob {raw.BlobId}; treated as superseded (specification 07 §3 rule 3)."));
        }

        return live;
    }

    /// <summary>
    /// Whether any live index entry locates <paramref name="objectId"/> —
    /// the segment-reuse test (NFR-PERF-010). Keyed by object identifier
    /// rather than content identifier so the answer survives a catalogue
    /// rebuild: locations come from durable deltas, while content mappings
    /// are catalogue-domain and lost with the file (02 §2).
    /// </summary>
    public bool HasLocation(ObjectId objectId)
    {
        var started = Stopwatch.GetTimestamp();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM object_locations l
                LEFT JOIN blobs b ON b.blob_id = l.blob_id
                WHERE l.object_id = $object AND COALESCE(b.state, 1) <> 3
            );
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        var exists = (long)command.ExecuteScalar()! > 0;
        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return exists;
    }

    /// <summary>
    /// The reuse candidate for <paramref name="objectId"/>, or
    /// <see langword="null"/> when no live index entry locates it — the
    /// trust-domain-aware form of <see cref="HasLocation"/> (ADR-0006;
    /// specification 09 §5).
    /// </summary>
    /// <remarks>
    /// The winner is chosen by the 07 §3 precedence order, the same order
    /// <see cref="ResolveLocation"/> uses, so the writer reported here is the
    /// writer of the entry a read would resolve to. One query rather than
    /// <see cref="ResolveLocation"/>'s two, and no damage finding: this runs
    /// once per segment on the NFR-PERF-010 path.
    /// </remarks>
    public ReuseCandidate? FindReuseCandidate(ObjectId objectId)
    {
        var started = Stopwatch.GetTimestamp();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT l.writer_id,
                   EXISTS (SELECT 1 FROM verified_objects v WHERE v.object_id = l.object_id)
            FROM object_locations l
            LEFT JOIN blobs b ON b.blob_id = l.blob_id
            WHERE l.object_id = $object AND COALESCE(b.state, 1) <> 3
            ORDER BY l.generation DESC, l.writer_id DESC, l.sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        using var reader = command.ExecuteReader();
        var candidate = reader.Read()
            ? new ReuseCandidate(WriterId.FromBytes((byte[])reader.GetValue(0)), reader.GetInt64(1) > 0)
            : null;

        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return candidate;
    }

    /// <summary>
    /// Remembers that <paramref name="objectId"/> was fetched, decrypted, and
    /// confirmed, so the next reuse does not pay for the read again.
    /// </summary>
    /// <remarks>
    /// Idempotent, because two segments in flight can verify the same object
    /// at once. Not restored by a rebuild — see <see cref="CatalogueSchema"/>.
    /// </remarks>
    public void RecordVerified(ObjectId objectId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO verified_objects (object_id) VALUES ($object);";
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Looks up a prior segment by content identifier — the dedup path (NFR-PERF-010).</summary>
    public ObjectId? LookupByContent(ContentId contentId)
    {
        var started = Stopwatch.GetTimestamp();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT object_id FROM segment_dedup WHERE content_id = $content;";
        command.Parameters.AddWithValue("$content", contentId.ToArray());

        var result = command.ExecuteScalar() is byte[] bytes ? ObjectId.FromBytes(bytes) : (ObjectId?)null;
        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return result;
    }

    /// <summary>The number of applied deltas — the idempotence ledger's size.</summary>
    public long AppliedDeltaCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM index_deltas;";
        return (long)command.ExecuteScalar()!;
    }

    private ResolvedLocation? QueryWinner(ObjectId objectId, bool excludeDeleted)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.blob_id,
                   CASE WHEN b.store_blob_key = b.blob_id THEN NULL ELSE b.store_blob_key END,
                   l.physical_offset, l.stored_length,
                   l.compression_profile, l.encryption_profile, l.generation, l.writer_id, l.sequence
            FROM object_locations l
            LEFT JOIN blobs b ON b.blob_id = l.blob_id
            WHERE l.object_id = $object
            {(excludeDeleted ? "AND COALESCE(b.state, 1) <> 3" : string.Empty)}
            ORDER BY l.generation DESC, l.writer_id DESC, l.sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ResolvedLocation(
            BlobId.FromBytes((byte[])reader.GetValue(0)),
            reader.IsDBNull(1) ? null : StoreBlobKey.FromBytes((byte[])reader.GetValue(1)),
            (ulong)reader.GetInt64(2),
            (uint)reader.GetInt64(3),
            (ushort)reader.GetInt64(4),
            (ushort)reader.GetInt64(5),
            (ulong)reader.GetInt64(6),
            WriterId.FromBytes((byte[])reader.GetValue(7)),
            (ulong)reader.GetInt64(8));
    }

    private void InsertLocation(
        SqliteTransaction transaction,
        IndexEntry entry,
        ulong generation,
        WriterId writerId,
        ulong sequence)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO object_locations
                (object_id, blob_id, physical_offset, stored_length, compression_profile,
                 encryption_profile, entry_type, generation, writer_id, sequence)
            VALUES ($object, $blob, $offset, $stored, $compression, $encryption, $type, $generation, $writer, $sequence);
            """;
        command.Parameters.AddWithValue("$object", entry.ObjectId.ToArray());
        command.Parameters.AddWithValue("$blob", entry.BlobId.ToArray());
        command.Parameters.AddWithValue("$offset", (long)entry.PhysicalOffset);
        command.Parameters.AddWithValue("$stored", (long)entry.StoredLength);
        command.Parameters.AddWithValue("$compression", entry.CompressionProfileValue);
        command.Parameters.AddWithValue("$encryption", entry.EncryptionProfileValue);
        command.Parameters.AddWithValue("$type", (int)entry.EntryType);
        command.Parameters.AddWithValue("$generation", (long)generation);
        command.Parameters.AddWithValue("$writer", writerId.ToArray());
        command.Parameters.AddWithValue("$sequence", (long)sequence);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Connect(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Unpooled. Microsoft.Data.Sqlite's pool marks a connection it hands
        // out active before it records who holds it, and a pool clear that
        // lands between the two disposes the connection under the caller
        // (ADR-0010 Amendment 3). An unpooled connection is in no pool for a
        // clear to reach, and lets go of its file when disposed.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        // WAL keeps every commit atomic and the file consistent through a
        // crash or a power loss; synchronous = NORMAL stops each commit
        // flushing the log to disk, so a power loss can take the newest
        // commits. That is the protection a cache gets (ADR-0010 Amendment 2):
        // it leaves the catalogue behind the store, which costs a rewrite,
        // never a restore. The default, FULL, flushes once for every row a
        // publication records, and a flush on Windows costs milliseconds.
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        pragmas.ExecuteNonQuery();

        return connection;
    }

    private static bool HasSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'catalogue_info';";
        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool IsCompatible(SqliteConnection connection, RepositoryId repositoryId)
    {
        if (!HasSchema(connection))
        {
            return true; // empty file: initialise in place
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM catalogue_info WHERE key IN ('schema_version', 'repository_id');";

            string? version = null, repository = null;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(0) == "schema_version")
                {
                    version = reader.GetString(1);
                }
                else
                {
                    repository = reader.GetString(1);
                }
            }

            return version == CatalogueSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && repository == Convert.ToHexStringLower(repositoryId.ToArray());
        }
        catch (SqliteException)
        {
            return false; // corrupt cache: rebuild
        }
    }

    /// <summary>
    /// The journal mode and synchronous level this connection runs with, as
    /// SQLite reports them: read back rather than assumed, because SQLite
    /// ignores a pragma it cannot honour instead of refusing it.
    /// </summary>
    internal (string JournalMode, long Synchronous) Durability()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = (string)command.ExecuteScalar()!;

        command.CommandText = "PRAGMA synchronous;";
        return (journalMode, (long)command.ExecuteScalar()!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Connection pooling keeps the file handle alive past Dispose, and
        // Windows cannot delete an open file — but a disposed catalogue
        // must be deletable: it is a cache, and drop-and-rebuild is its
        // whole lifecycle. Open() already clears pools before its own
        // rebuild delete; Dispose owes the same release.
        SqliteConnection.ClearPool(_connection);
        _connection.Dispose();
    }
}
