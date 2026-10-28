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
    /// <para>
    /// v7 over v6: <c>snapshots.consistency_method</c>, the manifest's key 10.
    /// It was recorded on every snapshot and read by nothing, while
    /// [specification 06 §6](../../specifications/repository-format/06-manifests.md)
    /// and [architecture 06 §5](../../docs/architecture/06-filesystem-capture.md)
    /// both said it was surfaced. A user restoring a database needs to know
    /// whether they hold a best-effort live capture or an application-consistent
    /// one, and until this column existed there was nowhere for that answer to
    /// come from without decoding a manifest per snapshot.
    /// </para>
    /// <para>
    /// v8 over v7: <c>version_contents</c> and <c>snapshot_structure</c>, the
    /// reverse of what the manifests say — which content objects each file
    /// version needs, and which records each snapshot is made of besides its
    /// files. They are what a damaged object's reach is traced through
    /// (FR-VER-005, specification 04 §7), and they are derived data every
    /// route that fills a catalogue writes: the capture, the projection and
    /// the forensic rebuild. Without them, naming what a damaged segment
    /// belongs to means opening every manifest of every snapshot.
    /// </para>
    /// <para>
    /// v9 over v8: <c>snapshots.observed_clock_skew_ms</c>, the manifest's key
    /// 14 — how far the capturing machine's clock stood from a peer's
    /// (NFR-TIME-002). Nullable, because a capture with no reading records
    /// none, and a row that read it as 0 would claim a clock in step.
    /// </para>
    /// <para>
    /// v10 over v9: <c>index_deltas.index_generation</c>, a delta's key 12
    /// (specification 07 §2.4), which a format-4 writer publishes one above
    /// the highest of. <c>object_locations.generation</c> keeps its name and
    /// now holds what an entry ranks by (07 §3): its delta's index
    /// generation, zero for a delta without one. A catalogue at v9 ranked by
    /// key generation, so it is rebuilt rather than read under the new rule.
    /// </para>
    /// <para>
    /// v11 over v10: <c>directory_access_times</c>, the access time each
    /// folder's tree states, so a capture can tell a folder whose only change
    /// is that it was read from one whose tree has to be written again
    /// (NFR-PERF-003). A folder's tree states its access time in the bytes its
    /// identity derives from, so without the prior value a capture cannot
    /// rebuild the prior identity, and every folder it read was written anew.
    /// </para>
    /// <para>
    /// v12 over v11: <c>object_locations</c> names its blob and its writer by
    /// numbers of the catalogue's own, from <c>location_blobs</c> and
    /// <c>location_writers</c>, and <c>located</c> reads each location by
    /// their ids again (NFR-PERF-011, ADR-0144). Each is sixteen bytes, and a
    /// location stated both twice, once in the row and once in the blob
    /// index, which holds the row's key.
    /// </para>
    /// <para>
    /// v13 over v12: no <c>segment_dedup</c>. Publication wrote each
    /// segment's content identifier beside its object there, and nothing
    /// read it: reuse looks a segment up by the object identifier its content
    /// identifier is keyed into, through <c>object_locations</c>
    /// (NFR-PERF-011, ADR-0145).
    /// </para>
    /// <para>
    /// v14 over v13: no <c>tree_entries.path_casefold</c> or its index. Each
    /// tree entry stated its path folded once in the row and again in the
    /// index, for a lookup that folds case, which now takes the fold a folder
    /// at a time through <c>ix_tree_entries_parent</c> when it is asked
    /// (NFR-PERF-011, ADR-0146).
    /// </para>
    /// </remarks>
    public const int Version = 14;

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
            merkle_root    BLOB,
            state          INTEGER NOT NULL DEFAULT 1
        ) WITHOUT ROWID;

        -- The blobs and writers a location names, each by a number given in
        -- the order the catalogue meets it. A number ranks nothing: 07 §3
        -- ranks by the writer's id, which `located` reads back.
        CREATE TABLE location_blobs (
            blob_ref INTEGER PRIMARY KEY,
            blob_id  BLOB NOT NULL UNIQUE
        );

        CREATE TABLE location_writers (
            writer_ref INTEGER PRIMARY KEY,
            writer_id  BLOB NOT NULL UNIQUE
        );

        CREATE TABLE object_locations (
            object_id           BLOB NOT NULL,
            blob_ref            INTEGER NOT NULL,
            physical_offset     INTEGER NOT NULL,
            stored_length       INTEGER NOT NULL,
            compression_profile INTEGER NOT NULL,
            encryption_profile  INTEGER NOT NULL,
            entry_type          INTEGER NOT NULL,
            generation          INTEGER NOT NULL,
            writer_ref          INTEGER NOT NULL,
            sequence            INTEGER NOT NULL,
            PRIMARY KEY (object_id, generation, writer_ref, sequence, blob_ref)
        ) WITHOUT ROWID;

        CREATE INDEX ix_locations_blob ON object_locations (blob_ref);

        CREATE VIEW located AS
            SELECT l.object_id, n.blob_id, l.physical_offset, l.stored_length, l.compression_profile,
                   l.encryption_profile, l.entry_type, l.generation, w.writer_id, l.sequence
            FROM object_locations l
            JOIN location_blobs n ON n.blob_ref = l.blob_ref
            JOIN location_writers w ON w.writer_ref = l.writer_ref;

        CREATE TABLE index_deltas (
            delta_id             BLOB PRIMARY KEY,
            writer_id            BLOB NOT NULL,
            sequence             INTEGER NOT NULL,
            generation           INTEGER NOT NULL,
            predecessor_delta_id BLOB,
            is_void              INTEGER NOT NULL DEFAULT 0,
            index_generation     INTEGER,
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
            captured_at            INTEGER NOT NULL DEFAULT 0,
            consistency_method     INTEGER NOT NULL DEFAULT 1,
            observed_clock_skew_ms INTEGER
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
            entry_kind    INTEGER NOT NULL,
            object_id     BLOB NOT NULL,
            PRIMARY KEY (snapshot_id, path)
        ) WITHOUT ROWID;

        CREATE INDEX ix_tree_entries_parent ON tree_entries (snapshot_id, parent, path, entry_kind, object_id);

        -- The access time a folder's head tree states, keyed by the tree: the
        -- identity derives from the bytes, so one tree states one access time
        -- wherever it is referenced. A tree that states none has no row.
        CREATE TABLE directory_access_times (
            object_id   BLOB PRIMARY KEY,
            accessed_at INTEGER NOT NULL
        ) WITHOUT ROWID;

        -- The content objects each file version needs — its segments and its
        -- alternate streams' content — keyed by the object: the reverse of the
        -- list the version's sealed manifest holds (FR-VER-005).
        CREATE TABLE version_contents (
            object_id  BLOB NOT NULL,
            version_id BLOB NOT NULL,
            PRIMARY KEY (object_id, version_id)
        ) WITHOUT ROWID;

        -- The records each snapshot is made of besides its files' own: its
        -- manifest, its policy and error manifests, and every tree manifest of
        -- its structure, continuations included. Damage to one reaches the
        -- snapshot as a whole: a restore that rebuilds from that copy loses
        -- whatever the record lists.
        CREATE TABLE snapshot_structure (
            object_id   BLOB NOT NULL,
            snapshot_id BLOB NOT NULL,
            PRIMARY KEY (object_id, snapshot_id)
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
