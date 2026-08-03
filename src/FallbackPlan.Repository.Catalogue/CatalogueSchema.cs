namespace FallbackPlan.Repository.Catalogue;

/// <summary>
/// The catalogue's SQLite schema (architecture 02 §7; FR-MAN-002,
/// FR-MAN-005, FR-MAN-006): a disposable cache of index and manifest state.
/// Any schema or repository mismatch drops and rebuilds — the catalogue is
/// never authoritative, so migration logic would be complexity spent
/// protecting nothing.
/// </summary>
public static class CatalogueSchema
{
    /// <summary>The current schema version; a mismatch rebuilds.</summary>
    public const int Version = 1;

    /// <summary>The complete DDL.</summary>
    public const string Ddl = """
        CREATE TABLE catalogue_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE blobs (
            blob_id        BLOB PRIMARY KEY,
            store_blob_key BLOB NOT NULL UNIQUE,
            blob_class     INTEGER NOT NULL,
            key_generation INTEGER NOT NULL,
            record_count   INTEGER NOT NULL,
            length         INTEGER NOT NULL,
            digest         BLOB,
            state          INTEGER NOT NULL DEFAULT 1
        ) WITHOUT ROWID;

        CREATE TABLE object_locations (
            object_id           BLOB NOT NULL,
            blob_id             BLOB NOT NULL,
            physical_offset     INTEGER NOT NULL,
            stored_length       INTEGER NOT NULL,
            compression_profile INTEGER NOT NULL,
            encryption_profile  INTEGER NOT NULL,
            entry_type          INTEGER NOT NULL,
            generation          INTEGER NOT NULL,
            writer_id           BLOB NOT NULL,
            sequence            INTEGER NOT NULL,
            PRIMARY KEY (object_id, generation, writer_id, sequence, blob_id)
        ) WITHOUT ROWID;

        CREATE INDEX ix_locations_blob ON object_locations (blob_id);

        CREATE TABLE index_deltas (
            delta_id             BLOB PRIMARY KEY,
            writer_id            BLOB NOT NULL,
            sequence             INTEGER NOT NULL,
            generation           INTEGER NOT NULL,
            predecessor_delta_id BLOB,
            is_void              INTEGER NOT NULL DEFAULT 0,
            UNIQUE (writer_id, sequence)
        ) WITHOUT ROWID;

        CREATE TABLE checkpoints (
            checkpoint_id             BLOB PRIMARY KEY,
            generation                INTEGER NOT NULL,
            writer_id                 BLOB NOT NULL,
            predecessor_checkpoint_id BLOB
        ) WITHOUT ROWID;

        CREATE TABLE checkpoint_watermarks (
            checkpoint_id    BLOB NOT NULL,
            writer_id        BLOB NOT NULL,
            highest_sequence INTEGER NOT NULL,
            PRIMARY KEY (checkpoint_id, writer_id)
        ) WITHOUT ROWID;

        CREATE TABLE snapshots (
            snapshot_id            BLOB PRIMARY KEY,
            device_id              BLOB NOT NULL,
            backup_set_id          BLOB NOT NULL,
            object_id              BLOB NOT NULL,
            root_tree              BLOB NOT NULL,
            publication_generation INTEGER NOT NULL,
            capture_status         INTEGER NOT NULL,
            signature_state        INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE file_versions (
            object_id       BLOB PRIMARY KEY,
            name            BLOB NOT NULL,
            entry_kind      INTEGER NOT NULL,
            logical_length  INTEGER NOT NULL,
            whole_file_hash BLOB NOT NULL,
            parent_version  BLOB,
            segment_count   INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE INDEX ix_file_versions_hash ON file_versions (whole_file_hash);

        CREATE TABLE segment_dedup (
            content_id BLOB PRIMARY KEY,
            object_id  BLOB NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE damage_findings (
            id        INTEGER PRIMARY KEY,
            kind      INTEGER NOT NULL,
            object_id BLOB,
            blob_id   BLOB,
            detail    TEXT NOT NULL
        );

        CREATE TABLE writer_state (
            writer_id        BLOB PRIMARY KEY,
            next_sequence    INTEGER NOT NULL,
            pending_sequence INTEGER,
            last_delta_id    BLOB
        ) WITHOUT ROWID;
        """;
}
