using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using System.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Data.Sqlite;

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
    ulong CapturedAt);

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
    ulong? IdentityFileId);

/// <summary>A resolved physical location: everything a targeted read needs to open one blob and one record.</summary>
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

    private Catalogue(SqliteConnection connection) => _connection = connection;

    /// <summary>
    /// Opens (or creates) the catalogue at <paramref name="path"/> for
    /// <paramref name="repositoryId"/>. Any mismatch — schema version or
    /// repository identity — deletes and recreates: it is a cache
    /// (FR-MAN-002).
    /// </summary>
    public static Catalogue Open(string path, RepositoryId repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var connection = Connect(path);

        if (!IsCompatible(connection, repositoryId))
        {
            connection.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            connection = Connect(path);
        }

        if (!HasSchema(connection))
        {
            using var create = connection.CreateCommand();
            create.CommandText = CatalogueSchema.Ddl;
            create.ExecuteNonQuery();

            using var stamp = connection.CreateCommand();
            stamp.CommandText = """
                INSERT INTO catalogue_info (key, value) VALUES
                ('schema_version', $version),
                ('repository_id', $repository),
                ('source', 'live');
                """;
            stamp.Parameters.AddWithValue("$version", CatalogueSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stamp.Parameters.AddWithValue("$repository", Convert.ToHexStringLower(repositoryId.ToArray()));
            stamp.ExecuteNonQuery();
        }

        return new Catalogue(connection);
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
        ArgumentNullException.ThrowIfNull(delta);

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

        transaction.Commit();
    }

    /// <summary>Applies one checkpoint idempotently, entries carrying the checkpoint's provenance.</summary>
    public void ApplyCheckpoint(CheckpointId checkpointId, Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

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
        ulong? identityFileId = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO file_versions
                (object_id, name, entry_kind, logical_length, whole_file_hash, parent_version, segment_count,
                 modified_at, identity_device, identity_file_id)
            VALUES ($id, $name, $kind, $length, $hash, $parent, $segments, $modified, $device, $fileid);
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
        ArgumentNullException.ThrowIfNull(path);
        return path.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Records one path of a snapshot's tree (schema v2 <c>tree_entries</c>).</summary>
    public void RecordTreeEntry(
        ReadOnlySpan<byte> snapshotId,
        string path,
        EntryKind entryKind,
        ObjectId objectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

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

    /// <summary>Every known snapshot, newest capture first.</summary>
    public IReadOnlyList<CatalogueSnapshot> EnumerateSnapshots()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, device_id, backup_set_id, object_id, root_tree,
                   publication_generation, capture_status, signature_state, captured_at
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
                (ulong)reader.GetInt64(8)));
        }

        return snapshots;
    }

    /// <summary>
    /// Resolves one path within a snapshot — the NFR-PERF-004 lookup.
    /// Case-insensitive resolution folds through the ADR-0026 §Decision 8
    /// key; an exact match always wins over a folded one.
    /// </summary>
    public CatalogueTreeEntry? LookupPath(ReadOnlySpan<byte> snapshotId, string path, bool caseInsensitive = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var started = Stopwatch.GetTimestamp();

        using var command = _connection.CreateCommand();
        command.CommandText = caseInsensitive
            ? """
              SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id
              FROM tree_entries t
              LEFT JOIN file_versions f ON f.object_id = t.object_id
              WHERE t.snapshot_id = $snapshot AND t.path_casefold = $casefold
              ORDER BY (t.path = $path) DESC, t.path
              LIMIT 1;
              """
            : """
              SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id
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
        ArgumentNullException.ThrowIfNull(parentPath);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT t.path, t.entry_kind, t.object_id, f.logical_length, f.modified_at, f.identity_device, f.identity_file_id
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

    private static CatalogueTreeEntry ReadTreeEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        (EntryKind)reader.GetInt64(1),
        ObjectId.FromBytes((byte[])reader.GetValue(2)),
        reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3),
        reader.IsDBNull(4) ? null : (ulong)reader.GetInt64(4),
        reader.IsDBNull(5) ? null : unchecked((ulong)reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : unchecked((ulong)reader.GetInt64(6)));

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
        ulong capturedAt = 0)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO snapshots
                (snapshot_id, device_id, backup_set_id, object_id, root_tree, publication_generation, capture_status, signature_state, captured_at)
            VALUES ($id, $device, $set, $object, $root, $generation, $status, $signature, $captured);
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
        command.ExecuteNonQuery();
    }

    /// <summary>Appends a damage finding (FR-MAN-011).</summary>
    public void RecordFinding(DamageFinding finding, ObjectId? objectId = null, BlobId? blobId = null)
    {
        ArgumentNullException.ThrowIfNull(finding);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO damage_findings (kind, object_id, blob_id, detail) VALUES ($kind, $object, $blob, $detail);
            """;
        command.Parameters.AddWithValue("$kind", (int)finding.Kind);
        command.Parameters.AddWithValue("$object", objectId is { } o ? o.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$blob", blobId is { } b ? b.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$detail", finding.Detail);
        command.ExecuteNonQuery();
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
            SELECT l.blob_id, b.store_blob_key, l.physical_offset, l.stored_length,
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

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
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

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
