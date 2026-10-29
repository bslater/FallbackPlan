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
/// One path within a snapshot (schema v17 <c>folder_entries</c>), joined with
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
/// One entry a folder's tree holds (schema v17 <c>folder_entries</c>;
/// ADR-0151): its name in the folder, its kind, and the object it names,
/// which for a folder is the folder's head tree.
/// </summary>
public readonly record struct CatalogueFolderEntry(string Name, EntryKind Kind, ObjectId ObjectId);

/// <summary>
/// A folder's head tree within one snapshot (schema v11), with the access time
/// that tree states, or <see langword="null"/> when it states none.
/// </summary>
public sealed record CatalogueDirectoryVersion(ObjectId TreeId, ulong? AccessedAt);

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
/// <see cref="Generation"/>, <see cref="WriterId"/> and <see cref="Sequence"/>
/// are the winning entry's rank (07 §3): its generation is its delta's index
/// generation, or zero for a delta that predates format 4. A delta a
/// checkpoint carries ranks as it would read from its own key.
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

/// <summary>What a destination holds of one snapshot's files (ADR-0088 Amendment 1).</summary>
/// <param name="Total">The snapshot's files.</param>
/// <param name="Held">Of them, those whose content is all at the destination.</param>
/// <param name="Whole">Whether it holds the snapshot itself as well: every file, and the record naming them.</param>
public sealed record FilesDelivered(long Total, long Held, bool Whole);

/// <summary>One blob a snapshot's content is read from.</summary>
/// <param name="BlobId">The blob.</param>
/// <param name="StoreBlobKey">Its keyed store name, or null where the catalogue holds none and a reader derives it.</param>
/// <param name="Class">Which namespace the blob lives under.</param>
public sealed record ContentBlob(BlobId BlobId, StoreBlobKey? StoreBlobKey, BlobClass Class);

/// <summary>A snapshot's files as the blobs their content is read from (ADR-0088 Amendment 1).</summary>
/// <param name="Files">The snapshot's files, those with no content included.</param>
/// <param name="Blobs">Every blob any of its files' content is read from.</param>
/// <param name="FileBlobs">For each file with content, the indexes into <paramref name="Blobs"/> it needs.</param>
public sealed record SnapshotContent(long Files, IReadOnlyList<ContentBlob> Blobs, IReadOnlyList<int[]> FileBlobs);

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
    /// <summary>
    /// An entry's path, as a column of a query that joins the folder
    /// <c>d</c> a walk reached to the entry <c>t</c> its tree holds.
    /// </summary>
    private const string ChildPath = "CASE d.prefix WHEN '' THEN t.name ELSE d.prefix || '/' || t.name END";

    /// <summary>
    /// The folders snapshot <c>$snapshot</c> holds, as the common table
    /// <c>folders</c> of each folder's head tree and its path: the root tree,
    /// then each folder named by an entry of one already reached
    /// (ADR-0151). A tree's identity derives from what it holds, so no tree
    /// reaches itself.
    /// </summary>
    private static readonly string SnapshotFolders = $"""
        WITH RECURSIVE folders(tree_id, prefix) AS (
            SELECT root_tree, '' FROM snapshots WHERE snapshot_id = $snapshot
            UNION ALL
            SELECT t.object_id, {ChildPath}
            FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
            WHERE t.entry_kind = {(int)EntryKind.DirectoryPlaceholder}
        )
        """;

    /// <summary>
    /// The folder snapshot <c>$snapshot</c> holds at the path
    /// <c>$folder</c>, as the common table <c>walk</c> of each folder's head
    /// tree and the rest of the path below it: its row with no rest left is
    /// that folder, and there is none when the snapshot holds no folder
    /// there. One statement, a key seek a name (ADR-0151).
    /// </summary>
    private static readonly string FolderAt = $"""
        WITH RECURSIVE walk(tree_id, rest) AS (
            SELECT root_tree, NULLIF($folder, '') FROM snapshots WHERE snapshot_id = $snapshot
            UNION ALL
            SELECT t.object_id, CASE instr(w.rest, '/') WHEN 0 THEN NULL ELSE substr(w.rest, instr(w.rest, '/') + 1) END
            FROM walk w CROSS JOIN folder_entries t
              ON t.tree_id = w.tree_id
             AND t.name = CASE instr(w.rest, '/') WHEN 0 THEN w.rest ELSE substr(w.rest, 1, instr(w.rest, '/') - 1) END
            WHERE w.rest IS NOT NULL AND t.entry_kind = {(int)EntryKind.DirectoryPlaceholder}
        )
        """;

    private readonly SqliteConnection _connection;
    private readonly ILogger _log;

    /// <summary>
    /// The folder the last lookup resolved, and its tree, so the files of
    /// one folder, which a capture asks for in turn, resolve it once. A
    /// snapshot's trees never change, and this catalogue's own writes to
    /// snapshots and folders clear it.
    /// </summary>
    private (byte[] Snapshot, string Folder, ObjectId? Tree)? _lastFolder;

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

        Log.CatalogueOpened(logger ?? NullLogger.Instance, repositoryId, new LogLabel(disposition));

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
                INSERT OR IGNORE INTO index_deltas
                    (delta_id, writer_id, sequence, generation, predecessor_delta_id, is_void, index_generation)
                VALUES ($id, $writer, $sequence, $generation, $predecessor, $void, $indexGeneration);
                """;
            ledger.Parameters.AddWithValue("$id", deltaId.ToArray());
            ledger.Parameters.AddWithValue("$writer", delta.WriterId.ToArray());
            ledger.Parameters.AddWithValue("$sequence", (long)delta.Sequence);
            ledger.Parameters.AddWithValue("$generation", (long)delta.Generation);
            ledger.Parameters.AddWithValue("$predecessor", delta.PredecessorDeltaId is { } p ? p.ToArray() : DBNull.Value);
            ledger.Parameters.AddWithValue("$void", delta.IsVoid ? 1 : 0);
            ledger.Parameters.AddWithValue(
                "$indexGeneration", delta.IndexGeneration is { } indexGeneration ? (long)indexGeneration : DBNull.Value);

            if (ledger.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return; // already applied
            }
        }

        foreach (var entry in delta.Entries)
        {
            InsertLocation(transaction, entry, delta.RankGeneration, delta.WriterId, delta.Sequence);
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

    /// <summary>Records one checkpoint idempotently: its ledger row and any watermarks it states, which nothing ranks by.</summary>
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

        // A checkpoint's own entries, the earlier form, are recorded nowhere:
        // they would rank at the checkpoint rather than where each delta
        // ranks (ADR-0008 Amendment 1, constraint 1), and the loader does not
        // apply them either. The deltas a format-5 checkpoint carries reach
        // the catalogue through ApplyDelta, as every other delta does.
        transaction.Commit();
    }

    /// <summary>
    /// Records what the catalogue is asked of a file version. The length,
    /// the optional modification time and identity are the NFR-PERF-003
    /// short-circuit's comparison key; identity is catalogue-domain only
    /// (02 §2) and absent after any rebuild. Nothing else a manifest states
    /// is kept, since nothing asks it of the catalogue (ADR-0147).
    /// </summary>
    public void RecordFileVersion(
        ObjectId objectId,
        ulong logicalLength,
        ulong? modifiedAt = null,
        ulong? identityDevice = null,
        ulong? identityFileId = null,
        bool hasAlternateStreams = false,
        ReadOnlyMemory<byte>? metadataDigest = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO file_versions
                (object_id, logical_length, modified_at, identity_device, identity_file_id, has_alternate_streams,
                 metadata_digest)
            VALUES ($id, $length, $modified, $device, $fileid, $streams, $metadata);
            """;
        command.Parameters.AddWithValue("$id", objectId.ToArray());
        command.Parameters.AddWithValue("$length", (long)logicalLength);
        command.Parameters.AddWithValue("$modified", modifiedAt is { } m ? (long)m : DBNull.Value);
        command.Parameters.AddWithValue("$device", identityDevice is { } d ? unchecked((long)d) : DBNull.Value);
        command.Parameters.AddWithValue("$fileid", identityFileId is { } f ? unchecked((long)f) : DBNull.Value);
        command.Parameters.AddWithValue("$streams", hasAlternateStreams ? 1 : 0);
        command.Parameters.AddWithValue(
            "$metadata", metadataDigest is { } digest ? digest.ToArray() : (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A path's fold: the invariant lowercase form of the NFC-normalised
    /// path — the same case regime rules-v1 matching uses (ADR-0026
    /// §Decision 8). Taken when a lookup or a plan asks for it, never stored
    /// (ADR-0146); stored paths keep their original form.
    /// </summary>
    public static string Casefold(string path)
    {
        ThrowHelper.ThrowIfNull(path);
        return path.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// Records what the folder tree <paramref name="treeId"/> holds, as
    /// <see cref="RecordFolders"/> records a snapshot's folders.
    /// </summary>
    public void RecordFolder(ObjectId treeId, IEnumerable<CatalogueFolderEntry> entries) =>
        RecordFolders([(treeId, entries)]);

    /// <summary>
    /// Records what each folder tree holds (schema v17 <c>folder_entries</c>;
    /// ADR-0151), in one transaction, passing over a tree whose entries are
    /// already recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tree's identity derives from what it holds, so a folder a snapshot
    /// left as it was is the tree an earlier snapshot recorded, and this one
    /// reaches the rows that one paid for. A tree's entries are recorded
    /// whole or not at all, so a tree that has any is passed over.
    /// </para>
    /// <para>
    /// Record the snapshot that reaches the folders first. Forgetting a
    /// snapshot lets go of every folder no recorded snapshot reaches
    /// (<see cref="ForgetSnapshots"/>), so a folder recorded before the row
    /// that reaches it could be let go of before that row lands.
    /// </para>
    /// </remarks>
    public void RecordFolders(IEnumerable<(ObjectId TreeId, IEnumerable<CatalogueFolderEntry> Entries)> folders)
    {
        ThrowHelper.ThrowIfNull(folders);
        _lastFolder = null;

        using var transaction = _connection.BeginTransaction();
        using var held = _connection.CreateCommand();
        held.Transaction = transaction;
        held.CommandText = "SELECT EXISTS (SELECT 1 FROM folder_entries WHERE tree_id = $tree);";
        var heldTree = held.Parameters.Add("$tree", SqliteType.Blob);

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO folder_entries (tree_id, name, entry_kind, object_id)
            VALUES ($tree, $name, $kind, $object);
            """;
        var tree = insert.Parameters.Add("$tree", SqliteType.Blob);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
        var named = insert.Parameters.Add("$object", SqliteType.Blob);
        foreach (var (treeId, entries) in folders)
        {
            ThrowHelper.ThrowIfNull(entries);
            heldTree.Value = tree.Value = treeId.ToArray();
            if ((long)held.ExecuteScalar()! > 0)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                ThrowHelper.ThrowIfNullOrEmpty(entry.Name);
                name.Value = entry.Name;
                kind.Value = (int)entry.Kind;
                named.Value = entry.ObjectId.ToArray();
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Binds the snapshot and the folder path a <see cref="FolderAt"/> walk
    /// follows. The root is the empty string.
    /// </summary>
    private static void BindFolder(SqliteCommand command, ReadOnlySpan<byte> snapshotId, string folderPath)
    {
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$folder", folderPath);
    }

    /// <summary>
    /// The head tree of the folder a snapshot holds at
    /// <paramref name="folderPath"/>, or <see langword="null"/> when it holds
    /// none there. The root is the empty string.
    /// </summary>
    private ObjectId? FolderTree(ReadOnlySpan<byte> snapshotId, string folderPath)
    {
        if (_lastFolder is { } last
            && string.Equals(last.Folder, folderPath, StringComparison.Ordinal)
            && snapshotId.SequenceEqual(last.Snapshot))
        {
            return last.Tree;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {FolderAt}
            SELECT tree_id FROM walk WHERE rest IS NULL;
            """;
        BindFolder(command, snapshotId, folderPath);
        var tree = command.ExecuteScalar() is byte[] bytes ? ObjectId.FromBytes(bytes) : (ObjectId?)null;
        _lastFolder = (snapshotId.ToArray(), folderPath, tree);
        return tree;
    }

    /// <summary>
    /// Records the access time each folder tree states (schema v11
    /// <c>directory_access_times</c>), in one transaction, so a later capture
    /// can rebuild the identity that tree has without reading it.
    /// </summary>
    /// <remarks>
    /// A writer records them after the row and the folders that reach them,
    /// as it does a snapshot's folders, so forgetting another snapshot in
    /// between cannot let go of a time this one states (ADR-0153).
    /// </remarks>
    public void RecordDirectoryAccessTimes(IEnumerable<(ObjectId TreeId, ulong AccessedAt)> times)
    {
        ThrowHelper.ThrowIfNull(times);

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO directory_access_times (object_id, accessed_at) VALUES ($object, $accessed);
            """;
        var tree = command.Parameters.Add("$object", SqliteType.Blob);
        var accessed = command.Parameters.Add("$accessed", SqliteType.Integer);
        foreach (var (treeId, accessedAt) in times)
        {
            tree.Value = treeId.ToArray();
            accessed.Value = unchecked((long)accessedAt);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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
        command.CommandText = "SELECT n.blob_id FROM location_blobs n WHERE EXISTS (SELECT 1 FROM object_locations l WHERE l.blob_ref = n.blob_ref);";

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
    /// The paths come from one walk of every snapshot's folders, since
    /// <c>folder_entries</c> keeps a folder's entries by its tree and not by
    /// version: the cost of every snapshot's listing, paid only when damage
    /// is outstanding.
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
        command.CommandText = "SELECT DISTINCT object_id FROM located WHERE blob_id = $blob;";
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
                WITH RECURSIVE folders(snapshot_id, tree_id, prefix) AS (
                    SELECT snapshot_id, root_tree, '' FROM snapshots
                    UNION ALL
                    SELECT d.snapshot_id, t.object_id, {ChildPath}
                    FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
                    WHERE t.entry_kind = {(int)EntryKind.DirectoryPlaceholder}
                )
                SELECT d.snapshot_id, {ChildPath} FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
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

    /// <summary>
    /// Forgets a snapshot the store no longer holds, as
    /// <see cref="ForgetSnapshots"/> forgets several.
    /// </summary>
    /// <param name="snapshotId">The snapshot's 16-byte id.</param>
    public void ForgetSnapshot(ReadOnlySpan<byte> snapshotId) => ForgetSnapshots([snapshotId.ToArray()]);

    /// <summary>
    /// Forgets snapshots the store no longer holds: their rows, the records
    /// they were made of, and every folder no snapshot left reaches, with its
    /// access time (FR-GC-013; ADR-0151, ADR-0153). The catalogue is a cache
    /// of the store, and a snapshot a person deleted is one nothing should
    /// list, offer to restore, or trace damage to. A folder another snapshot
    /// still holds stays, and so does what the forgotten snapshots' files
    /// shared.
    /// </summary>
    /// <remarks>
    /// Which folders are reached is found by walking every remaining
    /// snapshot from its root, a folder once however many hold it, so it
    /// costs a pass over the folders kept and is paid once for all the
    /// snapshots given; letting go of access times costs one more pass over
    /// the entries left. It is one transaction with the deletions, so a
    /// capture recording its folders sees either none of it or all of it.
    /// </remarks>
    /// <param name="snapshotIds">The snapshots' 16-byte ids.</param>
    public void ForgetSnapshots(IEnumerable<byte[]> snapshotIds)
    {
        ThrowHelper.ThrowIfNull(snapshotIds);

        var forgotten = snapshotIds.ToList();
        if (forgotten.Count == 0)
        {
            return;
        }

        _lastFolder = null;
        using var transaction = _connection.BeginTransaction();
        using (var forget = _connection.CreateCommand())
        {
            forget.Transaction = transaction;
            forget.CommandText = """
                DELETE FROM snapshot_structure WHERE snapshot_id = $snapshot;
                DELETE FROM snapshots WHERE snapshot_id = $snapshot;
                """;
            var snapshot = forget.Parameters.Add("$snapshot", SqliteType.Blob);
            foreach (var snapshotId in forgotten)
            {
                snapshot.Value = snapshotId;
                forget.ExecuteNonQuery();
            }
        }

        using (var release = _connection.CreateCommand())
        {
            release.Transaction = transaction;
            release.CommandText = $"""
                WITH RECURSIVE reached(tree_id) AS (
                    SELECT root_tree FROM snapshots
                    UNION
                    SELECT t.object_id FROM reached r CROSS JOIN folder_entries t ON t.tree_id = r.tree_id
                    WHERE t.entry_kind = {(int)EntryKind.DirectoryPlaceholder}
                )
                DELETE FROM folder_entries WHERE tree_id NOT IN (SELECT tree_id FROM reached);
                """;
            release.ExecuteNonQuery();

            // Every entry left now belongs to a reached folder, so the
            // reached folders are the roots and the folders those entries
            // name, an empty one included (ADR-0153).
            release.CommandText = $"""
                DELETE FROM directory_access_times WHERE object_id NOT IN (
                    SELECT root_tree FROM snapshots
                    UNION
                    SELECT object_id FROM folder_entries WHERE entry_kind = {(int)EntryKind.DirectoryPlaceholder});
                """;
            release.ExecuteNonQuery();
        }

        transaction.Commit();
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
        command.CommandText = $"""
            {SnapshotFolders}
            SELECT 1
            FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
            JOIN file_versions f ON f.object_id = t.object_id
            WHERE f.identity_file_id IS NOT NULL
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

        // A file no earlier version shares an identity with, which is every
        // new file, is answered from the identity index alone, before any
        // walk of the snapshot's folders.
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM file_versions WHERE identity_device = $device AND identity_file_id = $fileId);";
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$device", (long)device);
        command.Parameters.AddWithValue("$fileId", (long)fileId);

        CatalogueTreeEntry? entry = null;
        if ((long)command.ExecuteScalar()! > 0)
        {
            command.CommandText = $"""
                {SnapshotFolders}
                SELECT {ChildPath}, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
                FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
                JOIN file_versions f ON f.object_id = t.object_id
                WHERE f.identity_device = $device AND f.identity_file_id = $fileId
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            entry = reader.Read() ? ReadTreeEntry(reader) : null;
        }

        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return entry;
    }

    public CatalogueTreeEntry? LookupPath(ReadOnlySpan<byte> snapshotId, string path, bool caseInsensitive = false)
    {
        ThrowHelper.ThrowIfNullOrEmpty(path);
        var started = Stopwatch.GetTimestamp();

        // A path is names joined by a separator, so one that begins with a
        // separator names an empty one first, which no folder holds.
        CatalogueTreeEntry? entry = null;
        if ((caseInsensitive ? FoldedPath(snapshotId, path) : path) is { } found
            && found.LastIndexOf('/') is var slash && slash != 0
            && FolderTree(snapshotId, slash < 0 ? string.Empty : found[..slash]) is { } folder)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT $path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
                FROM folder_entries t
                LEFT JOIN file_versions f ON f.object_id = t.object_id
                WHERE t.tree_id = $tree AND t.name = $name;
                """;
            command.Parameters.AddWithValue("$tree", folder.ToArray());
            command.Parameters.AddWithValue("$path", found);
            command.Parameters.AddWithValue("$name", found[(slash + 1)..]);

            using var reader = command.ExecuteReader();
            entry = reader.Read() ? ReadTreeEntry(reader) : null;
        }

        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return entry;
    }

    /// <summary>
    /// The path in a snapshot whose every component folds as
    /// <paramref name="path"/>'s does, found a folder at a time through each
    /// folder's listing, since no fold is stored (ADR-0146): the path itself
    /// where the snapshot holds it, and otherwise the first such path in
    /// ordinal order. Every folder whose name folds alike is followed, since
    /// only one of them may hold the rest.
    /// </summary>
    private string? FoldedPath(ReadOnlySpan<byte> snapshotId, string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT root_tree FROM snapshots WHERE snapshot_id = $snapshot;";
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        if (command.ExecuteScalar() is not byte[] root)
        {
            return null;
        }

        command.CommandText = "SELECT name, object_id FROM folder_entries WHERE tree_id = $tree;";
        var tree = command.Parameters.Add("$tree", SqliteType.Blob);

        List<(string Path, byte[] Tree)> candidates = [(string.Empty, root)];
        foreach (var component in path.Split('/'))
        {
            var folded = Casefold(component);
            List<(string Path, byte[] Tree)> matching = [];
            foreach (var candidate in candidates)
            {
                tree.Value = candidate.Tree;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    if (string.Equals(Casefold(name), folded, StringComparison.Ordinal))
                    {
                        matching.Add((candidate.Path.Length == 0 ? name : candidate.Path + "/" + name, (byte[])reader.GetValue(1)));
                    }
                }
            }

            if (matching.Count == 0)
            {
                return null;
            }

            candidates = matching;
        }

        var found = candidates.ConvertAll(candidate => candidate.Path);
        return found.Contains(path, StringComparer.Ordinal) ? path : found.Min(StringComparer.Ordinal);
    }

    /// <summary>
    /// A folder's head tree within one snapshot, and the access time it
    /// states, or <see langword="null"/> when the snapshot holds no folder at
    /// <paramref name="path"/>. The root is the empty string.
    /// </summary>
    public CatalogueDirectoryVersion? LookupDirectory(ReadOnlySpan<byte> snapshotId, string path)
    {
        ThrowHelper.ThrowIfNull(path);

        if (FolderTree(snapshotId, path) is not { } tree)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT accessed_at FROM directory_access_times WHERE object_id = $tree;";
        command.Parameters.AddWithValue("$tree", tree.ToArray());
        return new CatalogueDirectoryVersion(tree, command.ExecuteScalar() is long accessedAt ? unchecked((ulong)accessedAt) : null);
    }

    /// <summary>
    /// Lists a directory's immediate children within a snapshot, in path
    /// order. The root is the empty string.
    /// </summary>
    public IReadOnlyList<CatalogueTreeEntry> ListDirectory(ReadOnlySpan<byte> snapshotId, string parentPath)
    {
        ThrowHelper.ThrowIfNull(parentPath);

        var entries = new List<CatalogueTreeEntry>();
        if (FolderTree(snapshotId, parentPath) is not { } tree)
        {
            return entries;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT CASE $parent WHEN '' THEN t.name ELSE $parent || '/' || t.name END, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
            FROM folder_entries t
            LEFT JOIN file_versions f ON f.object_id = t.object_id
            WHERE t.tree_id = $tree
            ORDER BY t.name;
            """;
        command.Parameters.AddWithValue("$tree", tree.ToArray());
        command.Parameters.AddWithValue("$parent", parentPath);

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
        command.CommandText = $"""
            {SnapshotFolders}
            SELECT {ChildPath} AS path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id, f.has_alternate_streams, f.metadata_digest
            FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
            LEFT JOIN file_versions f ON f.object_id = t.object_id
            WHERE t.entry_kind <> $directory
            ORDER BY path;
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
        command.CommandText = $"""
            {SnapshotFolders}
            SELECT COUNT(*) FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
            WHERE t.entry_kind <> $directory;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$directory", (long)EntryKind.DirectoryPlaceholder);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// What a destination holds of one snapshot's files, given the watermark
    /// its ledger records (ADR-0088 Amendment 1): it holds everything
    /// published at or before the snapshot that watermark names, so a file
    /// is held when every content object it needs was published by then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The watermark is a snapshot record's counter, which is its run's write
    /// intent, and the run publishes its content later, in its index delta.
    /// So the watermark is first moved to the delta that published the next
    /// snapshot record after it: that run's own, and the last whose content
    /// the destination was given. Content another writer published is taken
    /// as held, since this writer's counters say nothing of it.
    /// </para>
    /// <para>
    /// A file with no content needs nothing beyond the snapshot that names it.
    /// That record is what <see cref="FilesDelivered.Whole"/> waits on, and a
    /// copy sends it last.
    /// </para>
    /// </remarks>
    /// <param name="snapshotId">The snapshot counted against, ordinarily the set's newest.</param>
    /// <param name="writer">The writer whose counters the watermark is in.</param>
    /// <param name="syncedSequence">The destination's watermark; positive.</param>
    public FilesDelivered FilesDeliveredThrough(ReadOnlySpan<byte> snapshotId, WriterId writer, ulong syncedSequence)
    {
        ThrowHelper.ThrowIfZero(syncedSequence);

        var total = CountFiles(snapshotId);
        long through;
        using (var bound = _connection.CreateCommand())
        {
            bound.CommandText = """
                SELECT MIN(l.sequence) FROM snapshots s
                JOIN located l ON l.object_id = s.object_id
                WHERE l.writer_id = $writer AND l.sequence > $synced;
                """;
            bound.Parameters.AddWithValue("$writer", writer.ToArray());
            bound.Parameters.AddWithValue("$synced", (long)syncedSequence);
            through = bound.ExecuteScalar() is long sequence ? sequence : long.MaxValue;
        }

        using var missing = _connection.CreateCommand();
        missing.CommandText = $"""
            {SnapshotFolders}
            SELECT COUNT(*) FROM (
                SELECT DISTINCT d.prefix, t.name FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
                JOIN version_contents c ON c.version_id = t.object_id
                WHERE t.entry_kind <> $directory
                  AND NOT EXISTS (
                      SELECT 1 FROM located l
                      LEFT JOIN blobs b ON b.blob_id = l.blob_id
                      WHERE l.object_id = c.object_id
                        AND COALESCE(b.state, 1) <> 3
                        AND (l.writer_id <> $writer OR l.sequence <= $through)));
            """;
        missing.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        missing.Parameters.AddWithValue("$directory", (long)EntryKind.DirectoryPlaceholder);
        missing.Parameters.AddWithValue("$writer", writer.ToArray());
        missing.Parameters.AddWithValue("$through", through);
        var lacking = (long)(missing.ExecuteScalar() ?? 0L);

        using var record = _connection.CreateCommand();
        record.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM snapshots s
                JOIN located l ON l.object_id = s.object_id
                WHERE s.snapshot_id = $snapshot AND (l.writer_id <> $writer OR l.sequence <= $through));
            """;
        record.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        record.Parameters.AddWithValue("$writer", writer.ToArray());
        record.Parameters.AddWithValue("$through", through);
        var named = (long)record.ExecuteScalar()! > 0;

        return new FilesDelivered(total, total - lacking, named && lacking == 0);
    }

    /// <summary>
    /// One snapshot's files as the blobs their content is read from (ADR-0088
    /// Amendment 1): what a sync counts as content lands. Each content object
    /// is taken from its winning live location, the blob a restore reads it
    /// from (07 §3).
    /// </summary>
    /// <param name="snapshotId">The snapshot, ordinarily the set's newest.</param>
    public SnapshotContent ContentOf(ReadOnlySpan<byte> snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {SnapshotFolders}
            SELECT {ChildPath}, c.object_id, l.blob_id, b.store_blob_key, b.blob_class,
                   l.generation, l.writer_id, l.sequence
            FROM folders d CROSS JOIN folder_entries t ON t.tree_id = d.tree_id
            JOIN version_contents c ON c.version_id = t.object_id
            JOIN located l ON l.object_id = c.object_id
            LEFT JOIN blobs b ON b.blob_id = l.blob_id
            WHERE t.entry_kind <> $directory AND COALESCE(b.state, 1) <> 3;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToArray());
        command.Parameters.AddWithValue("$directory", (long)EntryKind.DirectoryPlaceholder);

        // Per object, its winner by 07 §3's order; per file, the objects it needs.
        var winners = new Dictionary<ObjectId, (ContentBlob Blob, ulong Generation, byte[] Writer, ulong Sequence)>();
        var needs = new Dictionary<string, HashSet<ObjectId>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var objectId = ObjectId.FromBytes((byte[])reader.GetValue(1));
                var blobBytes = (byte[])reader.GetValue(2);
                var blobId = BlobId.FromBytes(blobBytes);

                // A blob the catalogue knows only as a placeholder carries its
                // own id as its store key and no class; a reader derives the
                // key, and file content is only ever in data blobs.
                StoreBlobKey? storeKey = reader.IsDBNull(3) || ((byte[])reader.GetValue(3)).AsSpan().SequenceEqual(blobBytes)
                    ? null
                    : StoreBlobKey.FromBytes((byte[])reader.GetValue(3));
                var blobClass = !reader.IsDBNull(4) && (BlobClass)reader.GetInt64(4) == BlobClass.Metadata
                    ? BlobClass.Metadata
                    : BlobClass.Data;
                var generation = (ulong)reader.GetInt64(5);
                var writer = (byte[])reader.GetValue(6);
                var sequence = (ulong)reader.GetInt64(7);

                if (!needs.TryGetValue(path, out var objects))
                {
                    needs[path] = objects = [];
                }

                objects.Add(objectId);
                var candidate = (new ContentBlob(blobId, storeKey, blobClass), generation, writer, sequence);
                if (!winners.TryGetValue(objectId, out var standing) || Outranks(candidate, standing))
                {
                    winners[objectId] = candidate;
                }
            }
        }

        var blobs = new List<ContentBlob>();
        var indexOf = new Dictionary<BlobId, int>();
        var fileBlobs = new List<int[]>();
        foreach (var objects in needs.Values)
        {
            var needed = new HashSet<int>();
            foreach (var objectId in objects)
            {
                var blob = winners[objectId].Blob;
                if (!indexOf.TryGetValue(blob.BlobId, out var index))
                {
                    indexOf[blob.BlobId] = index = blobs.Count;
                    blobs.Add(blob);
                }

                needed.Add(index);
            }

            fileBlobs.Add([.. needed]);
        }

        return new SnapshotContent(CountFiles(snapshotId), blobs, fileBlobs);

        static bool Outranks(
            (ContentBlob Blob, ulong Generation, byte[] Writer, ulong Sequence) candidate,
            (ContentBlob Blob, ulong Generation, byte[] Writer, ulong Sequence) standing) =>
            Catalogue.Outranks(
                (candidate.Generation, candidate.Writer, candidate.Sequence),
                (standing.Generation, standing.Writer, standing.Sequence));
    }

    /// <summary>
    /// Whether one location outranks another by 07 §3's order: the later
    /// generation, then the writer whose id sorts higher, then the later
    /// sequence.
    /// </summary>
    private static bool Outranks(
        (ulong Generation, byte[] Writer, ulong Sequence) candidate,
        (ulong Generation, byte[] Writer, ulong Sequence) standing) =>
        candidate.Generation != standing.Generation
            ? candidate.Generation > standing.Generation
            : candidate.Writer.AsSpan().SequenceCompareTo(standing.Writer) is var order && order != 0
                ? order > 0
                : candidate.Sequence > standing.Sequence;

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
        _lastFolder = null;
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
    /// The location <see cref="ResolveLocation"/> resolves
    /// <paramref name="objectId"/> to, without the rule-3 finding it records.
    /// </summary>
    /// <remarks>
    /// For a writer asking whether an entry it has yet to publish would
    /// outrank the one that holds an object (ADR-0017 Amendment 2). That is
    /// asked of records whose resolution the same pass has already read
    /// through <see cref="ResolveLocation"/>, and a second finding for one
    /// damaged entry would report it twice.
    /// </remarks>
    public ResolvedLocation? WinningLocation(ObjectId objectId) => QueryWinner(objectId, excludeDeleted: true);

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
                JOIN location_blobs n ON n.blob_ref = l.blob_ref
                LEFT JOIN blobs b ON b.blob_id = n.blob_id
                WHERE l.object_id = $object AND COALESCE(b.state, 1) <> 3
            );
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        var exists = (long)command.ExecuteScalar()! > 0;
        EngineDiagnostics.CatalogueLookupDuration.Record(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        return exists;
    }

    /// <summary>
    /// Whether a live index entry that <paramref name="writerId"/> published
    /// locates <paramref name="objectId"/>: the writer's own copy, whether or
    /// not it is the one the 07 §3 order picks.
    /// </summary>
    /// <remarks>
    /// Live as <see cref="HasLocation"/> counts it, so a copy whose blob is
    /// known deleted is no copy. Keyed by the entry's <c>writer_id</c>, which a
    /// rebuild recovers, so the answer survives losing the catalogue.
    /// </remarks>
    public bool HasLocationBy(ObjectId objectId, WriterId writerId)
    {
        var started = Stopwatch.GetTimestamp();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM object_locations l
                JOIN location_blobs n ON n.blob_ref = l.blob_ref
                LEFT JOIN blobs b ON b.blob_id = n.blob_id
                WHERE l.object_id = $object
                  AND l.writer_ref = (SELECT writer_ref FROM location_writers WHERE writer_id = $writer)
                  AND COALESCE(b.state, 1) <> 3
            );
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.Parameters.AddWithValue("$writer", writerId.ToArray());
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
            SELECT l.generation, (SELECT writer_id FROM location_writers WHERE writer_ref = l.writer_ref), l.sequence,
                   EXISTS (SELECT 1 FROM verified_objects v WHERE v.object_id = l.object_id)
            FROM object_locations l
            JOIN location_blobs n ON n.blob_ref = l.blob_ref
            LEFT JOIN blobs b ON b.blob_id = n.blob_id
            WHERE l.object_id = $object AND COALESCE(b.state, 1) <> 3;
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        // Ranked here, by the 07 §3 order: an ORDER BY on the writer's id,
        // which a location holds by number, sorts on every lookup.
        ReuseCandidate? candidate = null;
        (ulong Generation, byte[] Writer, ulong Sequence) standing = default;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rank = (Generation: (ulong)reader.GetInt64(0), Writer: (byte[])reader.GetValue(1), Sequence: (ulong)reader.GetInt64(2));
            if (candidate is null || Outranks(rank, standing))
            {
                standing = rank;
                candidate = new ReuseCandidate(WriterId.FromBytes(rank.Writer), reader.GetInt64(3) > 0);
            }
        }

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

    /// <summary>The number of applied deltas — the idempotence ledger's size.</summary>
    public long AppliedDeltaCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM index_deltas;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The highest index generation any applied delta carries, or zero when
    /// none carries one (07 §2.4): what a format-4 writer publishes one
    /// above.
    /// </summary>
    public ulong MaxIndexGeneration()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(index_generation), 0) FROM index_deltas;";
        return (ulong)(long)command.ExecuteScalar()!;
    }

    private ResolvedLocation? QueryWinner(ObjectId objectId, bool excludeDeleted)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.blob_id,
                   CASE WHEN b.store_blob_key = b.blob_id THEN NULL ELSE b.store_blob_key END,
                   l.physical_offset, l.stored_length,
                   l.compression_profile, l.encryption_profile, l.generation,
                   (SELECT writer_id FROM location_writers WHERE writer_ref = l.writer_ref), l.sequence
            FROM object_locations l
            JOIN location_blobs n ON n.blob_ref = l.blob_ref
            LEFT JOIN blobs b ON b.blob_id = n.blob_id
            WHERE l.object_id = $object
            {(excludeDeleted ? "AND COALESCE(b.state, 1) <> 3" : string.Empty)};
            """;
        command.Parameters.AddWithValue("$object", objectId.ToArray());

        // Ranked here, by the 07 §3 order: an ORDER BY on the writer's id,
        // which a location holds by number, sorts on every lookup.
        ResolvedLocation? winner = null;
        (ulong Generation, byte[] Writer, ulong Sequence) standing = default;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rank = (Generation: (ulong)reader.GetInt64(6), Writer: (byte[])reader.GetValue(7), Sequence: (ulong)reader.GetInt64(8));
            if (winner is null || Outranks(rank, standing))
            {
                standing = rank;
                winner = new ResolvedLocation(
                    BlobId.FromBytes((byte[])reader.GetValue(0)),
                    reader.IsDBNull(1) ? null : StoreBlobKey.FromBytes((byte[])reader.GetValue(1)),
                    (ulong)reader.GetInt64(2),
                    (uint)reader.GetInt64(3),
                    (ushort)reader.GetInt64(4),
                    (ushort)reader.GetInt64(5),
                    rank.Generation,
                    WriterId.FromBytes(rank.Writer),
                    rank.Sequence);
            }
        }

        return winner;
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
            INSERT OR IGNORE INTO location_blobs (blob_id) VALUES ($blob);
            INSERT OR IGNORE INTO location_writers (writer_id) VALUES ($writer);
            INSERT OR IGNORE INTO object_locations
                (object_id, blob_ref, physical_offset, stored_length, compression_profile,
                 encryption_profile, entry_type, generation, writer_ref, sequence)
            VALUES ($object, (SELECT blob_ref FROM location_blobs WHERE blob_id = $blob), $offset, $stored,
                    $compression, $encryption, $type, $generation,
                    (SELECT writer_ref FROM location_writers WHERE writer_id = $writer), $sequence);
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
