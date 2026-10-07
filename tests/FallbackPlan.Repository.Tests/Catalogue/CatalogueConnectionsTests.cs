using FallbackPlan.Domain.Identifiers;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// Two connections to one catalogue can be in use at once (FR-SVC-012,
/// ADR-0010 Amendment 5). A write behind the other connection's waits for it
/// to commit rather than failing. A read is neither kept waiting by the other
/// connection's write nor shown it before it commits.
/// </summary>
/// <remarks>
/// The service gives a set's backup the archive's catalogue connection to
/// itself, and everything that may run beside the backup — its sync,
/// retention, a deletion, a heal — opens one of its own. That is sound only
/// while SQLite keeps the connections apart. In WAL mode one connection
/// writes at a time and a reader sees the last commit, and
/// Microsoft.Data.Sqlite retries a busy database until the command times
/// out, thirty seconds by default. A catalogue that answered busy at once
/// would turn every overlap into a failed retention run or a failed backup.
/// </remarks>
[TestClass]
public sealed class CatalogueConnectionsTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-catalogue-connections", Guid.NewGuid().ToString("n"));

    private string CataloguePath => Path.Combine(_root, "catalogue.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [TestMethod]
    public async Task AWrite_BehindAnotherConnectionsWrite_WaitsForItToCommit_ThenCommits()
    {
        // Created here, so marked as needing a rebuild until something says
        // it has been.
        using var backups = Catalogue.Open(CataloguePath, Repo);
        using var another = Catalogue.Open(CataloguePath, Repo);
        Assert.IsTrue(backups.NeedsRebuild);

        using var held = new HeldWrite(CataloguePath, "UPDATE catalogue_info SET value = value WHERE key = 'source';");
        var marking = Task.Run(another.MarkRebuilt);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(
            marking.IsCompleted,
            marking.IsFaulted
                ? $"a write behind another connection's must wait for it, not fail: {marking.Exception!.InnerException!.Message}"
                : "a write behind another connection's must wait for it, not go around it");

        held.Transaction.Commit();
        await marking.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsFalse(backups.NeedsRebuild, "what the other connection wrote is the catalogue the backup's reads");
    }

    [TestMethod]
    public async Task ARead_BesideAnotherConnectionsUncommittedWrite_SeesTheLastCommit_WithoutWaiting()
    {
        using var reader = Catalogue.Open(CataloguePath, Repo);

        // Uncommitted: on the reader's own connection it would already show.
        using var held = new HeldWrite(CataloguePath, "DELETE FROM catalogue_info WHERE key = 'rebuild_pending';");
        var reading = Task.Run(() => reader.NeedsRebuild);

        Assert.IsTrue(
            await reading.WaitAsync(TimeSpan.FromSeconds(20)),
            "a read beside another connection's write sees the last commit, not the write");
    }

    /// <summary>
    /// A third connection holding the write lock over a change it has not
    /// committed, as a backup's connection holds one while it projects a
    /// snapshot.
    /// </summary>
    private sealed class HeldWrite : IDisposable
    {
        private readonly SqliteConnection _connection;

        public HeldWrite(string path, string sql)
        {
            _connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            _connection.Open();
            Transaction = _connection.BeginTransaction();
            using var write = _connection.CreateCommand();
            write.Transaction = Transaction;
            write.CommandText = sql;
            write.ExecuteNonQuery();
        }

        public SqliteTransaction Transaction { get; }

        public void Dispose()
        {
            Transaction.Dispose();
            _connection.Dispose();
        }
    }
}
