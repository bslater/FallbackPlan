using System.Runtime.CompilerServices;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The committed fixture repository (F7; NFR-COMP-004): regeneration from
/// the conformance constants is byte-identical to the committed copy — any
/// diff is a format change that must be deliberate — and the committed bytes
/// open, restore, verify, and rebuild with the current reader.
/// </summary>
public sealed class FixtureRepositoryTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        // The binary's location anchors first: ContinuousIntegrationBuild
        // maps [CallerFilePath] to /_/…, which does not exist on a CI
        // runner; the source path remains the fallback.
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.NotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v1");
    }

    private static string? LocateRoot(string? start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    private static SortedDictionary<string, string> FileMap(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')] = path;
        }

        return map;
    }

    [Fact]
    public async Task Regeneration_is_byte_identical_to_the_committed_fixture()
    {
        var committed = CommittedFixturePath();
        var regenerated = Path.Combine(_scratch, "regenerated");

        await FixtureRepository.GenerateAsync(regenerated, CancellationToken.None);

        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly — a green run must never silently create what it
            // was supposed to compare against.
            await FixtureRepository.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed fixture was absent and has been generated at {committed} — review and commit it.");
        }

        var committedFiles = FileMap(committed);
        var regeneratedFiles = FileMap(regenerated);

        Assert.Equal(committedFiles.Keys, regeneratedFiles.Keys);
        foreach (var (relative, committedPath) in committedFiles)
        {
            var committedBytes = await File.ReadAllBytesAsync(committedPath);
            var regeneratedBytes = await File.ReadAllBytesAsync(regeneratedFiles[relative]);
            Assert.True(
                committedBytes.AsSpan().SequenceEqual(regeneratedBytes),
                $"'{relative}' differs from the committed fixture — a format change must be deliberate (NFR-COMP-004).");
        }
    }

    [Fact]
    public async Task The_committed_fixture_opens_restores_verifies_and_rebuilds()
    {
        var store = new LocalFileSystemObjectStore(CommittedFixturePath());
        using var passphrase = FixtureRepository.CreatePassphrase();

        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        Assert.Equal(FixtureRepository.Repo, repository.RepositoryId);
        Assert.True(repository.UnstableFormatWarning);
        Assert.True(repository.KdfBelowCreationMinimums, "the fixture's KDF parameters are deliberately small");

        // Restore the fixture file byte-identically through the ordinary path.
        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var manifestEntry = reader.AllRecords.Single(record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
        var manifestRead = await reader.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
        Assert.Equal(RecordReadOutcome.Ok, manifestRead.Outcome);

        var manifest = Format.Manifests.FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
        using var restored = new MemoryStream();
        var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
        Assert.True(restore.Success, restore.FailureDetail);
        Assert.Equal(FixtureRepository.FileContent(), restored.ToArray());

        // Every blob verifies at the deepest level.
        using var verifier = new VerifyEngine(repository.RepositoryId, repository.Keys, store);
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, VerifyLevel.EveryRecord, CancellationToken.None);
            Assert.True(result.Ok, $"{entry.Key.Value}: {result.Detail}");
        }

        // The index plane loads and rebuilds a catalogue with zero findings.
        using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_scratch, "catalogue.db"), repository.RepositoryId);
        var loader = new IndexLoader(store, repository.RepositoryId, repository.Hierarchy);
        var report = await new CatalogueRebuilder(loader).RebuildAsync(
            catalogue, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);

        Assert.Equal(1, report.DeltasApplied);
        Assert.Empty(report.Findings);
        Assert.NotNull(catalogue.ResolveLocation(manifestEntry.ObjectId));

        // The journal holds the intent and its retirement, both verifiable.
        using var journalReader = new JournalReader(store, repository.RepositoryId, repository.Hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        Assert.Equal(0, unparseable);
        Assert.Equal(2, records.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
