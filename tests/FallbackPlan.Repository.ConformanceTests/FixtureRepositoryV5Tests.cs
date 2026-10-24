using System.Runtime.CompilerServices;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue;
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
/// The committed <c>fixture-repository-v5</c> bytes
/// ([ADR-0097](../../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md);
/// NFR-COMP-004, FR-MAN-008, FR-MAN-009): format 5 changes the checkpoint and
/// nothing else. The descriptor names format 5 and requires
/// <c>delta-checkpoints</c>, so a reader that predates it refuses by name.
/// The blobs are format 3's, stamped 3, and the delta is format 4's. The
/// checkpoint carries that delta's sealed bytes, so a load reads the delta
/// from the checkpoint rather than from its key, reaches the same index
/// either way, and the file restores byte for byte.
/// </summary>
/// <remarks>Does not establish FR-MAN-017: nothing here deletes a delta.</remarks>
[TestClass]
public sealed class FixtureRepositoryV5Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v5-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v5");
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

    private static async Task<string> CommittedFixtureAsync(bool bootstrap)
    {
        var committed = CommittedFixturePath();
        Assert.IsTrue(bootstrap || Directory.Exists(committed), $"The committed v5 fixture is absent at {committed}.");
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly. A green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV5.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v5 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        return committed;
    }

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var authority = FixtureRepositoryV5.DeriveAuthority();
        using var loader = new IndexLoader(store, FixtureRepositoryV5.Repo, authority.Credential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    [TestMethod]
    public async Task FixtureRepositoryV5_TheCommittedBytes_DeclareFormatFive_CarryTheirDeltaInACheckpoint_AndRestore()
    {
        var committed = await CommittedFixtureAsync(bootstrap: true);
        var store = new LocalFileSystemObjectStore(committed);

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.AreEqual(FormatVersions.DeltaCheckpoints, descriptor.FormatVersion);
        Assert.Contains(RepositoryDescriptorCodec.FeatureRelocatableRecords, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureIndexGenerations, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureDeltaCheckpoints, descriptor.RequiredFeatures);

        // Nothing in a blob changed, so every blob is stamped 3.
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, Math.Min(BlobEnvelope.MaxLength, entry.Length)), CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);
            Assert.AreEqual(FormatVersions.RelocatableRecords, BlobEnvelope.Parse(memory.ToArray()).FormatVersion);
        }

        var counting = new CountingObjectStore(store);
        var state = await LoadAsync(counting);
        Assert.IsEmpty(state.Findings);

        var checkpoint = Assert.ContainsSingle(state.Checkpoints).Checkpoint;
        var deltaId = Assert.ContainsSingle(checkpoint.SubsumedDeltaIds);
        Assert.ContainsSingle(checkpoint.SubsumedDeltas);
        Assert.IsEmpty(checkpoint.Entries);
        Assert.IsEmpty(checkpoint.WriterWatermarks);

        var delta = Assert.ContainsSingle(state.Deltas);
        Assert.AreEqual(deltaId, delta.DeltaId);
        Assert.AreEqual(1UL, delta.Delta.IndexGeneration);
        Assert.IsTrue(state.Resolved.Values.All(entry => entry.Generation == 1));
        Assert.IsEmpty(
            counting.ReadKeys.Where(key => key.StartsWith("index/delta/", StringComparison.Ordinal)),
            "The delta the checkpoint carries was fetched from its key anyway.");

        using (var authority = FixtureRepositoryV5.DeriveAuthority())
        {
            using var loader = new IndexLoader(store, FixtureRepositoryV5.Repo, authority.Credential);
            using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_scratch, "catalogue.db"), FixtureRepositoryV5.Repo);
            var report = await new CatalogueRebuilder(loader)
                .RebuildAsync(catalogue, currentGeneration: 0, IndexLoader.GapPatienceGenerations,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.IsEmpty(report.Findings);
            Assert.AreEqual(1UL, catalogue.MaxIndexGeneration());
        }

        using (var passphrase = FixtureRepositoryV5.CreatePassphrase())
        {
            var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
                store, passphrase, CancellationToken.None);
            using (repository)
            using (authority)
            {
                Assert.AreEqual(FormatVersions.DeltaCheckpoints, repository.EffectiveFormatVersion);

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
                SequenceAssert.AreEqual(FixtureRepositoryV5.FileContent(), restored.ToArray());
            }
        }
    }

    [TestMethod]
    public async Task FixtureRepositoryV5_WithTheDeltaGoneFromItsKey_StillLoadsTheSameIndex()
    {
        // The copy inside the checkpoint is the delta (07 §5): a store that
        // lost the object at the delta's key, or a listing that omits it,
        // loses nothing a reader needs. Only the test above bootstraps the
        // fixture, so the two never generate it at once.
        var committed = await CommittedFixtureAsync(bootstrap: false);
        var copy = Path.Combine(_scratch, "fixture");
        CopyDirectory(committed, copy);
        var whole = await LoadAsync(new LocalFileSystemObjectStore(committed));

        var store = new LocalFileSystemObjectStore(copy);
        var deltaKey = MetadataStoreKeys.IndexDelta(0, Assert.ContainsSingle(whole.Deltas).DeltaId);
        Assert.AreEqual(DeleteOutcome.Deleted, (await store.DeleteAsync(deltaKey, DeleteConditions.None, CancellationToken.None)).Outcome);

        var state = await LoadAsync(store);

        Assert.IsEmpty(state.Findings);
        Assert.IsTrue(whole.AllEntries.ToHashSet().SetEquals(state.AllEntries));
        Assert.HasCount(whole.Resolved.Count, state.Resolved);
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
