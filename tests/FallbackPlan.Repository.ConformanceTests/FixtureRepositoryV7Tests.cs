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
/// The committed <c>fixture-repository-v7</c> bytes
/// ([ADR-0197](../../../docs/adr/0197-format-7-a-failure-says-what-stands-in-its-place.md);
/// NFR-COMP-004, FR-MAN-003): format 7 adds a key to a capture failure and
/// changes nothing else. The descriptor names format 7 and requires
/// <c>stand-in-failures</c>, so a reader that predates it refuses by name.
/// The blobs are format 3's, stamped 3, the index format 5's, its delta
/// carried in a checkpoint, and the manifests format 6's. The snapshot is
/// partial, and its one failure names the file it holds and says, in key 4,
/// that what stands there is what the snapshot before held; the file
/// restores byte for byte.
/// </summary>
/// <remarks>Does not establish FR-MAN-017: nothing here deletes a delta.</remarks>
[TestClass]
public sealed class FixtureRepositoryV7Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v7-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v7");
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
        Assert.IsTrue(bootstrap || Directory.Exists(committed), $"The committed v7 fixture is absent at {committed}.");
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly. A green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV7.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v7 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        return committed;
    }

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var authority = FixtureRepositoryV7.DeriveAuthority();
        using var loader = new IndexLoader(store, FixtureRepositoryV7.Repo, authority.Credential);
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
    public async Task FixtureRepositoryV7_TheCommittedBytes_DeclareFormatSeven_SayWhatStandsInAFailuresPlace_AndRestore()
    {
        var committed = await CommittedFixtureAsync(bootstrap: true);
        var store = new LocalFileSystemObjectStore(committed);

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.AreEqual(FormatVersions.StandInFailures, descriptor.FormatVersion);
        Assert.Contains(RepositoryDescriptorCodec.FeatureRelocatableRecords, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureIndexGenerations, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureDeltaCheckpoints, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureFileFlagsAndLinkForms, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureStandInFailures, descriptor.RequiredFeatures);

        // Nothing in a blob's container changed, so every blob is stamped 3.
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, Math.Min(BlobEnvelope.MaxLength, entry.Length)), CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, CancellationToken.None);
            Assert.AreEqual(FormatVersions.RelocatableRecords, BlobEnvelope.Parse(memory.ToArray()).FormatVersion);
        }

        // The index is format 5's: its one delta is carried in a checkpoint.
        var state = await LoadAsync(store);
        Assert.IsEmpty(state.Findings);
        Assert.ContainsSingle(Assert.ContainsSingle(state.Checkpoints).Checkpoint.SubsumedDeltas);

        using (var authority = FixtureRepositoryV7.DeriveAuthority())
        {
            using var loader = new IndexLoader(store, FixtureRepositoryV7.Repo, authority.Credential);
            using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_scratch, "catalogue.db"), FixtureRepositoryV7.Repo);
            var report = await new CatalogueRebuilder(loader)
                .RebuildAsync(catalogue, currentGeneration: 0, IndexLoader.GapPatienceGenerations,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.IsEmpty(report.Findings);
        }

        using var passphrase = FixtureRepositoryV7.CreatePassphrase();
        var (repository, openedAuthority) = await RepositoryLifecycle.OpenForReadAsync(
            store, passphrase, CancellationToken.None);
        using (repository)
        using (openedAuthority)
        {
            Assert.AreEqual(FormatVersions.StandInFailures, repository.EffectiveFormatVersion);

            using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, openedAuthority);
            await reader.LoadBlobsAsync(CancellationToken.None);

            List<FileVersionManifest> manifests = [];
            foreach (var record in reader.AllRecords.Where(record => record.ObjectType == ObjectType.FileVersionManifest))
            {
                var read = await reader.ReadSegmentAsync(record.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
                manifests.Add(FileVersionManifestCodec.Decode(read.Plaintext!));
            }

            Assert.HasCount(2, manifests);

            var file = manifests.Single(manifest => manifest.EntryKind == EntryKind.File);
            Assert.AreEqual(FileFlagBits.NoDump, file.Metadata.FileFlags);
            Assert.AreEqual(LinkForm.SymbolicLink, file.LinkForm);
            using var restored = new MemoryStream();
            var restore = await new RestoreEngine(reader).RestoreFileAsync(file, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            SequenceAssert.AreEqual(FixtureRepositoryV7.FileContent(), restored.ToArray());

            var junction = manifests.Single(manifest => manifest.EntryKind == EntryKind.Symlink);
            Assert.AreEqual(LinkForm.MountPoint, junction.LinkForm);
            Assert.AreEqual(FileFlagBits.None, junction.Metadata.FileFlags);
            Assert.AreEqual(
                FixtureRepositoryBuilder.JunctionTarget,
                System.Text.Encoding.UTF8.GetString(junction.LinkTarget!.Value.Span));

            // The snapshot is partial, and its one failure names the file
            // the tree holds, saying what stands there is what the snapshot
            // before held.
            var snapshotRecord = Assert.ContainsSingle(reader.AllRecords.Where(record => record.ObjectType == ObjectType.SnapshotManifest));
            var snapshotRead = await reader.ReadSegmentAsync(snapshotRecord.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, snapshotRead.Outcome);
            var snapshot = SnapshotManifestCodec.Decode(snapshotRead.Plaintext!).Manifest;
            Assert.AreEqual((byte)2, snapshot.CaptureStatus);

            var errorRead = await reader.ReadSegmentAsync(snapshot.ErrorManifest!.Value, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, errorRead.Outcome);
            var failure = Assert.ContainsSingle(ErrorManifestCodec.Decode(errorRead.Plaintext!).Failures);
            Assert.AreEqual("fixture.bin", System.Text.Encoding.UTF8.GetString(Assert.ContainsSingle(failure.PathComponents).Span));
            Assert.AreEqual(CaptureFailureReason.IoError, failure.Reason);
            Assert.IsTrue(failure.StandsIn);
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
