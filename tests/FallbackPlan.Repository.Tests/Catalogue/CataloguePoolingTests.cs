using System.Collections.Concurrent;
using FallbackPlan.Domain.Identifiers;
using Microsoft.Data.Sqlite;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// A catalogue's connection is the catalogue's own, whatever the rest of the
/// process does with SQLite's connection pools (FR-MAN-002).
/// </summary>
/// <remarks>
/// The service opens catalogues from many operations at once, and three of
/// its paths — a restore source closing, a catalogue recreated for another
/// repository, an adoption — clear every pool in the process so that a file
/// they are about to delete is no longer held open. The pool in
/// Microsoft.Data.Sqlite marks a connection it hands out active before it
/// records who holds it, and a clear that falls between the two takes the
/// connection for a leaked one and disposes it under the caller, whose next
/// command meets a disposed handle. A drill that met one said nothing at all,
/// because a disposed object is what a drill cut short by shutdown meets
/// too.
/// </remarks>
[TestClass]
// Clears every SQLite pool in the process for seconds at a time, which is
// the hazard under test: run beside another suite, it would be that suite's
// hazard too.
[DoNotParallelize]
public sealed class CataloguePoolingTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-catalogue-pooling", Guid.NewGuid().ToString("n"));

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
    public async Task ACatalogueOpenedWhileTheProcessClearsItsPools_IsNeverHandedADisposedConnection()
    {
        // Bounded by time rather than by count: the window is two field writes
        // wide, so what finds it is opens per second, and the machine decides
        // how many that is.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clearers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                SqliteConnection.ClearAllPools();
            }
        })).ToArray();

        var failures = new ConcurrentQueue<Exception>();
        var opens = 0L;
        var openers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            var path = Path.Combine(_root, $"catalogue-{worker}.db");
            while (!stop.IsCancellationRequested && failures.IsEmpty)
            {
                try
                {
                    using var catalogue = Catalogue.Open(path, Repo);
                    _ = catalogue.EnumerateSnapshots();
                    Interlocked.Increment(ref opens);
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }
        })).ToArray();

        await Task.WhenAll(openers);
        await stop.CancelAsync();
        await Task.WhenAll(clearers);

        Assert.IsTrue(
            failures.IsEmpty,
            $"after {Interlocked.Read(ref opens)} clean opens: {(failures.TryPeek(out var first) ? first.ToString() : "")}");
        Assert.IsGreaterThan(0L, Interlocked.Read(ref opens), "the drill opened nothing, so it proved nothing");
    }
}
