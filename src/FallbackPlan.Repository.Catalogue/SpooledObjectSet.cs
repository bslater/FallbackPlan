using FallbackPlan.Domain.Identifiers;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Catalogue;

/// <summary>
/// A set of object identifiers kept on disk rather than in memory: the
/// records an archive session has stored in blobs it has uploaded, which a
/// later duplicate in the same session must find (ADR-0201). The session
/// keeps in memory only the claims still in flight; everything else is here.
/// </summary>
/// <remarks>
/// <para>
/// One SQLite table, keyed by the identifier, in a file in the session's
/// spool, created by the session and deleted when it disposes this. Nothing
/// in it needs to survive a crash, which a new session does not resume from,
/// so it keeps no journal and never waits on a flush; and it is the session's
/// alone, so it holds its lock for as long as it is open.
/// </para>
/// <para>
/// A lookup is a point read of the key, microseconds against the
/// milliseconds a segment takes to prepare. The page cache it reads through
/// is fixed, so what the set costs in memory does not grow with what it
/// holds. Lookups and additions may come from several threads at once.
/// </para>
/// </remarks>
public sealed class SpooledObjectSet : IDisposable
{
    /// <summary>The most of the set's pages kept in memory, in kibibytes.</summary>
    private const int PageCacheKiB = 1024;

    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _contains;
    private readonly SqliteCommand _insert;
    private readonly Lock _gate = new();

    private SpooledObjectSet(string path, SqliteConnection connection)
    {
        _path = path;
        _connection = connection;

        _contains = connection.CreateCommand();
        _contains.CommandText = "SELECT 1 FROM written WHERE id = $id;";
        _contains.Parameters.Add("$id", SqliteType.Blob);

        _insert = connection.CreateCommand();
        _insert.CommandText = "INSERT OR IGNORE INTO written (id) VALUES ($id);";
        _insert.Parameters.Add("$id", SqliteType.Blob);
    }

    /// <summary>Creates an empty set at <paramref name="path"/>, which must not exist.</summary>
    public static SpooledObjectSet Create(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Unpooled, so that disposing it lets go of the file and the file can
        // be deleted; a pooled connection stays open behind its owner.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText =
                "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA locking_mode = EXCLUSIVE; " +
                $"PRAGMA cache_size = -{PageCacheKiB}; PRAGMA temp_store = FILE; " +
                "CREATE TABLE written (id BLOB PRIMARY KEY) WITHOUT ROWID;";
            setup.ExecuteNonQuery();
            return new SpooledObjectSet(path, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Whether <paramref name="objectId"/> is in the set.</summary>
    public bool Contains(ObjectId objectId)
    {
        lock (_gate)
        {
            _contains.Parameters[0].Value = objectId.ToArray();
            return _contains.ExecuteScalar() is not null;
        }
    }

    /// <summary>Adds every identifier in <paramref name="objectIds"/>, in one transaction.</summary>
    public void AddRange(IEnumerable<ObjectId> objectIds)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            _insert.Transaction = transaction;
            foreach (var objectId in objectIds)
            {
                _insert.Parameters[0].Value = objectId.ToArray();
                _insert.ExecuteNonQuery();
            }

            transaction.Commit();
            _insert.Transaction = null;
        }
    }

    /// <summary>Closes the set and deletes its file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _contains.Dispose();
            _insert.Dispose();
            _connection.Dispose();
        }

        File.Delete(_path);
    }
}
