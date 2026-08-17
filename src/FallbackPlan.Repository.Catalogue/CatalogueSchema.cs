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
    /// <remarks>
    /// <para>
    /// v4 over v3: <c>verified_objects</c>, the verify-on-reuse memory
    /// ([ADR-0006](../../docs/adr/0006-object-identifiers-and-dedup-trust-domains.md)).
    /// It is deliberately not recoverable by a rebuild — see the table's own
    /// note.
    /// </para>
    /// <para>
    /// v3 over v2: <c>ix_tree_entries_parent</c> became a covering index —
    /// without it SQLite prefers the primary key's free ordering and scans
    /// the whole snapshot per directory listing (measured 15 ms/op at 100k
    /// files; covered, 0.28 ms). A version bump because a cache is never
    /// migrated, only rebuilt.
    /// </para>
    /// <para>
    /// v5 over v4: <c>file_versions.has_alternate_streams</c>, so
    /// <c>RestorePlanner</c> can declare the <c>alternate-streams</c>
    /// degradation (RR-6) without reading a manifest per file. On
    /// <c>file_versions</c> rather than <c>tree_entries</c> so the covering
    /// parent index is untouched and the flag rides the existing join.
    /// </para>
    /// <para>
    /// v6 over v5: <c>file_versions.metadata_digest</c>, so an incremental
    /// capture can tell "the bytes are unchanged" apart from "nothing about
    /// this file changed". Without it, a <c>chmod</c> — which moves ctime and
    /// not mtime — passed every signal reuse is keyed on, and the new mode was
    /// discarded. On <c>file_versions</c> beside the other reuse inputs, so
    /// the covering parent index is untouched.
    /// </para>
    /// </remarks>
    public const int Version = 6;

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
            signature_state        INTEGER NOT NULL,
            captured_at            INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;

        CREATE TABLE file_versions (
            object_id             BLOB PRIMARY KEY,
            name                  BLOB NOT NULL,
            entry_kind            INTEGER NOT NULL,
            logical_length        INTEGER NOT NULL,
            whole_file_hash       BLOB NOT NULL,
            parent_version        BLOB,
            segment_count         INTEGER NOT NULL,
            modified_at           INTEGER,
            identity_device       INTEGER,
            identity_file_id      INTEGER,
            has_alternate_streams INTEGER NOT NULL DEFAULT 0,
            metadata_digest       BLOB
        ) WITHOUT ROWID;

        CREATE INDEX ix_file_versions_hash ON file_versions (whole_file_hash);

        -- Finds a prior version by the file's stable identity rather than by
        -- its path, which is what makes a rename or a move recognisable as
        -- the same file instead of a delete plus a create (architecture 06 §1).
        CREATE INDEX ix_file_versions_identity
            ON file_versions (identity_device, identity_file_id);

        CREATE TABLE tree_entries (
            snapshot_id   BLOB NOT NULL,
            path          TEXT NOT NULL,
            parent        TEXT NOT NULL,
            path_casefold TEXT NOT NULL,
            entry_kind    INTEGER NOT NULL,
            object_id     BLOB NOT NULL,
            PRIMARY KEY (snapshot_id, path)
        ) WITHOUT ROWID;

        CREATE INDEX ix_tree_entries_parent ON tree_entries (snapshot_id, parent, path, entry_kind, object_id);

        CREATE INDEX ix_tree_entries_casefold ON tree_entries (snapshot_id, path_casefold);

        CREATE TABLE segment_dedup (
            content_id BLOB PRIMARY KEY,
            object_id  BLOB NOT NULL
        ) WITHOUT ROWID;

        -- Objects another writer stored that this device has fetched,
        -- decrypted, and confirmed (ADR-0006). Reuse in the `repository`
        -- domain consults it so the verification read is paid once per
        -- object rather than once per backup.
        --
        -- A rebuild does not restore it, and that is the accepted cost: the
        -- alternative is a durable repository object recording verification
        -- outcomes, which is format surface designed before anything consumes
        -- it. Losing the catalogue therefore re-imposes verification, once.
        CREATE TABLE verified_objects (
            object_id BLOB PRIMARY KEY
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
