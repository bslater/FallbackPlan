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
/// The committed <c>fixture-repository-v6</c> bytes
/// ([ADR-0184](../../../docs/adr/0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md);
/// NFR-COMP-004, FR-MAN-003): format 6 adds two manifest keys and changes
/// nothing else. The descriptor names format 6 and requires
/// <c>file-flags-and-link-forms</c>, so a reader that predates it refuses by
/// name. The blobs are format 3's, stamped 3, and the index format 5's, its
/// delta carried in a checkpoint. The file's manifest keeps its no-dump flag
/// in metadata key 11 and restores byte for byte; the junction's manifest
/// names it a mount point in key 14.
/// </summary>
/// <remarks>Does not establish FR-MAN-017: nothing here deletes a delta.</remarks>
[TestClass]
public sealed class FixtureRepositoryV6Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v6-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v6");
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
        Assert.IsTrue(bootstrap || Directory.Exists(committed), $"The committed v6 fixture is absent at {committed}.");
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly. A green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV6.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v6 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        return committed;
    }

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var authority = FixtureRepositoryV6.DeriveAuthority();
        using var loader = new IndexLoader(store, FixtureRepositoryV6.Repo, authority.Credential);
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
    public async Task FixtureRepositoryV6_TheCommittedBytes_DeclareFormatSix_KeepAFlagAndAMountPoint_AndRestore()
    {
        var committed = await CommittedFixtureAsync(bootstrap: true);
        var store = new LocalFileSystemObjectStore(committed);

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.AreEqual(FormatVersions.FileFlagsAndLinkForms, descriptor.FormatVersion);
        Assert.Contains(RepositoryDescriptorCodec.FeatureRelocatableRecords, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureIndexGenerations, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureDeltaCheckpoints, descriptor.RequiredFeatures);
        Assert.Contains(RepositoryDescriptorCodec.FeatureFileFlagsAndLinkForms, descriptor.RequiredFeatures);

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

        using (var authority = FixtureRepositoryV6.DeriveAuthority())
        {
            using var loader = new IndexLoader(store, FixtureRepositoryV6.Repo, authority.Credential);
            using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_scratch, "catalogue.db"), FixtureRepositoryV6.Repo);
            var report = await new CatalogueRebuilder(loader)
                .RebuildAsync(catalogue, currentGeneration: 0, IndexLoader.GapPatienceGenerations,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.IsEmpty(report.Findings);
        }

        using var passphrase = FixtureRepositoryV6.CreatePassphrase();
        var (repository, openedAuthority) = await RepositoryLifecycle.OpenForReadAsync(
            store, passphrase, CancellationToken.None);
        using (repository)
        using (openedAuthority)
        {
            Assert.AreEqual(FormatVersions.FileFlagsAndLinkForms, repository.EffectiveFormatVersion);

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
            SequenceAssert.AreEqual(FixtureRepositoryV6.FileContent(), restored.ToArray());

            var junction = manifests.Single(manifest => manifest.EntryKind == EntryKind.Symlink);
            Assert.AreEqual(LinkForm.MountPoint, junction.LinkForm);
            Assert.AreEqual(FileFlagBits.None, junction.Metadata.FileFlags);
            Assert.AreEqual(
                FixtureRepositoryBuilder.JunctionTarget,
                System.Text.Encoding.UTF8.GetString(junction.LinkTarget!.Value.Span));
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
