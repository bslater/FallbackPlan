using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Catalogue;

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

    /// <summary>Records a file-version manifest's projection and its segments' dedup mappings.</summary>
    public void RecordFileVersion(
        ObjectId objectId,
        ReadOnlySpan<byte> name,
        EntryKind entryKind,
        ulong logicalLength,
        ReadOnlySpan<byte> wholeFileHash,
        ObjectId? parentVersion,
        int segmentCount)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO file_versions
                (object_id, name, entry_kind, logical_length, whole_file_hash, parent_version, segment_count)
            VALUES ($id, $name, $kind, $length, $hash, $parent, $segments);
            """;
        command.Parameters.AddWithValue("$id", objectId.ToArray());
        command.Parameters.AddWithValue("$name", name.ToArray());
        command.Parameters.AddWithValue("$kind", (int)entryKind);
        command.Parameters.AddWithValue("$length", (long)logicalLength);
        command.Parameters.AddWithValue("$hash", wholeFileHash.ToArray());
        command.Parameters.AddWithValue("$parent", parentVersion is { } p ? p.ToArray() : DBNull.Value);
        command.Parameters.AddWithValue("$segments", segmentCount);
        command.ExecuteNonQuery();
    }

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
        int signatureState)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO snapshots
                (snapshot_id, device_id, backup_set_id, object_id, root_tree, publication_generation, capture_status, signature_state)
            VALUES ($id, $device, $set, $object, $root, $generation, $status, $signature);
            """;
        command.Parameters.AddWithValue("$id", snapshotId.ToArray());
        command.Parameters.AddWithValue("$device", deviceId.ToArray());
        command.Parameters.AddWithValue("$set", backupSetId.ToArray());
        command.Parameters.AddWithValue("$object", objectId.ToArray());
        command.Parameters.AddWithValue("$root", rootTree.ToArray());
        command.Parameters.AddWithValue("$generation", (long)publicationGeneration);
        command.Parameters.AddWithValue("$status", captureStatus);
        command.Parameters.AddWithValue("$signature", signatureState);
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

    /// <summary>Looks up a prior segment by content identifier — the dedup path (NFR-PERF-010).</summary>
    public ObjectId? LookupByContent(ContentId contentId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT object_id FROM segment_dedup WHERE content_id = $content;";
        command.Parameters.AddWithValue("$content", contentId.ToArray());

        return command.ExecuteScalar() is byte[] bytes ? ObjectId.FromBytes(bytes) : null;
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
