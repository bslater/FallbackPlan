using FallbackPlan.Repository;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// What the tests that cut a pass in front of each write check after every
/// cut, and the world they reset to before the next one
/// (<see cref="DiesBeforeWriteStore"/>).
/// </summary>
internal static class CutOracle
{
    /// <summary>Copies a store's directory, so each cut starts from the same world.</summary>
    public static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }

    /// <summary>Puts <paramref name="path"/> back to what <paramref name="pristine"/> holds.</summary>
    public static void ResetTo(string pristine, string path)
    {
        Directory.Delete(path, recursive: true);
        CopyDirectory(pristine, path);
    }

    /// <summary>Every object a store's directory holds, by its key.</summary>
    public static List<string> StoredKeys(string path) =>
        [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>The snapshots a store lists, by id.</summary>
    public static async Task<List<string>> ListedSnapshotsAsync(string storeRoot, string passphraseText)
    {
        var store = new LocalFileSystemObjectStore(storeRoot);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, passphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None);
        return [.. survey.Snapshots.Select(snapshot => snapshot.Fact.SnapshotId)];
    }

    /// <summary>
    /// Every snapshot the store lists, restored from that store alone through
    /// a reader built now, against the files its day backed up. The set's
    /// catalogue plans each restore, and the store's own index finds the
    /// bytes.
    /// </summary>
    /// <param name="storeRoot">The staging archive or a replica of it.</param>
    /// <param name="stateDirectory">The installation's state, which holds the set's catalogue.</param>
    /// <param name="passphraseText">The passphrase that reads the archive.</param>
    /// <param name="days">Each snapshot's day, by id.</param>
    /// <param name="files">The files a day backed up, by name.</param>
    /// <param name="scratch">Where restores are written.</param>
    /// <param name="at">The cut, for the assertion messages.</param>
    public static async Task AssertEveryListedSnapshotRestoresAsync(
        string storeRoot,
        string stateDirectory,
        string passphraseText,
        IReadOnlyDictionary<string, int> days,
        Func<int, IReadOnlyDictionary<string, byte[]>> files,
        string scratch,
        string at)
    {
        var store = new LocalFileSystemObjectStore(storeRoot);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, passphraseText, CancellationToken.None);
        var repository = opened.Repository;

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, opened.Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var catalogue = CatalogueDb.Open(
            Path.Combine(stateDirectory, $"catalogue-{repository.RepositoryId}.db"), repository.RepositoryId);
        var target = RestoreTargetProfile.ForLocalPlatform();

        var survey = await StagingMark.SurveyAsync(store, repository, CancellationToken.None);
        Assert.IsNotEmpty(survey.Snapshots, $"{at}: no snapshot is listed");
        foreach (var snapshot in survey.Snapshots)
        {
            var day = days[snapshot.Fact.SnapshotId];
            var output = Path.Combine(scratch, Guid.NewGuid().ToString("n"));
            var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                RestorePlanner.Plan(catalogue, Convert.FromHexString(snapshot.Fact.SnapshotId), string.Empty, target),
                output,
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = $"cut-{day}",
                    NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                CancellationToken.None);

            Assert.AreEqual(
                RestoreOutcome.Complete, receipt.Outcome, $"{at}: day {day}'s snapshot, still listed, did not restore");
            foreach (var (name, bytes) in files(day))
            {
                Assert.IsTrue(
                    bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, name))),
                    $"{at}: day {day}'s {name} came back different");
            }
        }
    }
}
