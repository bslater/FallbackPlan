using System.Runtime.CompilerServices;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The committed <c>fixture-repository-v4</c> bytes
/// ([ADR-0096](../../../docs/adr/0096-format-4-an-index-generation-per-delta.md);
/// NFR-COMP-004, FR-MAN-015): format 4 changes the index delta and nothing
/// else. The descriptor names format 4 and requires
/// <c>index-generations</c>, so a reader that predates it refuses by name.
/// The blobs are format 3's, stamped 3. The one delta carries index
/// generation 1, and the file restores byte for byte.
/// </summary>
[TestClass]
public sealed class FixtureRepositoryV4Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v4-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v4");
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

    [TestMethod]
    public async Task FixtureRepositoryV4_TheCommittedBytes_DeclareFormatFour_RankByIndexGeneration_AndRestore()
    {
        var committed = CommittedFixturePath();
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly. A green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV4.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v4 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        var store = new LocalFileSystemObjectStore(committed);

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.AreEqual(FormatVersions.IndexGenerations, descriptor.FormatVersion);
        Assert.Contains(RepositoryDescriptorCodec.FeatureRelocatableRecords, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureIndexGenerations, descriptor.RequiredFeatures);

        // Nothing in a blob changed, so every blob is stamped 3.
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, Math.Min(BlobEnvelope.MaxLength, entry.Length)), CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);
            Assert.AreEqual(FormatVersions.RelocatableRecords, BlobEnvelope.Parse(memory.ToArray()).FormatVersion);
        }

        using (var authority = FixtureRepositoryV4.DeriveAuthority())
        {
            using var loader = new IndexLoader(store, FixtureRepositoryV4.Repo, authority.Credential);
            var state = await loader.LoadAsync(
                currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
                CancellationToken.None);
            Assert.IsEmpty(state.Findings);
            var delta = Assert.ContainsSingle(state.Deltas).Delta;
            Assert.AreEqual(1UL, delta.IndexGeneration);
            Assert.IsTrue(state.Resolved.Values.All(entry => entry.Generation == 1));

            using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_scratch, "catalogue.db"), FixtureRepositoryV4.Repo);
            var report = await new CatalogueRebuilder(loader)
                .RebuildAsync(catalogue, currentGeneration: 0, IndexLoader.GapPatienceGenerations,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.IsEmpty(report.Findings);
            Assert.AreEqual(1UL, catalogue.MaxIndexGeneration());
        }

        using (var passphrase = FixtureRepositoryV4.CreatePassphrase())
        {
            var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
                store, passphrase, CancellationToken.None);
            using (repository)
            using (authority)
            {
                Assert.AreEqual(FormatVersions.IndexGenerations, repository.EffectiveFormatVersion);

                using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
                await reader.LoadBlobsAsync(CancellationToken.None);

                var manifestEntry = reader.AllRecords.Single(
                    record => record.ObjectType == ObjectType.FileVersionManifest);
                var manifestRead = await reader.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);

                var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
                using var restored = new MemoryStream();
                var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
                Assert.IsTrue(restore.Success, restore.FailureDetail);
                SequenceAssert.AreEqual(FixtureRepositoryV4.FileContent(), restored.ToArray());
            }
        }
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
