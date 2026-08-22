using System.Runtime.CompilerServices;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The committed write-only fixture repository (ADR-0042; NFR-COMP-004).
/// Sealing is randomised by design — a fresh content key and ephemeral share
/// per blob — so unlike the v1 fixture there is no byte-identical
/// regeneration to compare against; what the committed bytes freeze is the
/// READ contract: the descriptor verifies by derive-and-compare, the write
/// bundle alone opens structure and answers <c>ContentSealed</c> for
/// content, the derived authority restores the deterministic file
/// byte-identically, and the wrong passphrase opens nothing.
/// </summary>
[TestClass]
public sealed class FixtureRepositoryV2Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v2-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v2");
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
    public async Task FixtureRepositoryV2_TheCommittedBytes_HoldTheWriteOnlyReadContract()
    {
        var committed = CommittedFixturePath();
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly — a green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV2.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v2 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        var store = new LocalFileSystemObjectStore(committed);

        // No key object anywhere in the store (FR-WOR-001).
        Assert.IsFalse(Directory.Exists(Path.Combine(committed, "keys")), "a write-only repository stores no key object");

        // The descriptor names format 2, demands the sealed-data-plane
        // feature, and its public key is the derive-and-compare verifier.
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.IsTrue(RepositoryLifecycle.IsWriteOnly(descriptor));
        Assert.Contains(
            Format.Descriptor.RepositoryDescriptorCodec.FeatureSealedDataPlane, descriptor.RequiredFeatures);

        using (var wrong = Passphrase.Create("not the fixture passphrase"))
        {
            Assert.IsFalse(
                RepositoryLifecycle.TryDeriveReadAuthority(descriptor, wrong, out _),
                "the wrong passphrase must not reproduce the fixture's keys");
        }

        // The write bundle alone — the service's posture — opens structure
        // and refuses content honestly.
        using (var authority = FixtureRepositoryV2.DeriveAuthority())
        {
            using var writeOnly = await RepositoryLifecycle.OpenWriteOnlyAsync(
                store, authority.Credential, CancellationToken.None);
            using var structural = new RepositoryReader(writeOnly.RepositoryId, writeOnly.Keys, store);
            await structural.LoadBlobsAsync(CancellationToken.None);

            var manifestEntry = structural.AllRecords.Single(
                record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
            var manifestRead = await structural.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome, "the structure plane reads with the bundle alone");
            var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            var sealedRead = await structural.ReadSegmentAsync(
                manifest.SegmentReferences[0].ObjectId, CancellationToken.None);
            Assert.AreEqual(
                RecordReadOutcome.ContentSealed, sealedRead.Outcome,
                "content must answer sealed, not readable and not damaged, without a grant");

            // The structure plane also rebuilds the catalogue and reads the
            // journal — the hub's bookkeeping never needed content.
            using var catalogue = Catalogue.Catalogue.Open(
                Path.Combine(_scratch, "catalogue.db"), writeOnly.RepositoryId);
            var report = await new CatalogueRebuilder(new IndexLoader(store, writeOnly.RepositoryId, writeOnly.Hierarchy))
                .RebuildAsync(catalogue, currentGeneration: 0, gapPatienceGenerations: 2,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.AreEqual(1, report.DeltasApplied);
            Assert.IsEmpty(report.Findings);

            using var journalReader = new JournalReader(store, writeOnly.RepositoryId, writeOnly.Hierarchy);
            var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
            Assert.AreEqual(0, unparseable);
            Assert.AreEqual(2, records.Count);
        }

        // The passphrase-derived authority restores the deterministic file
        // byte-identically — the whole promise of the format.
        using (var passphrase = FixtureRepositoryV2.CreatePassphrase())
        {
            var (repository, authority) = await RepositoryLifecycle.OpenWriteOnlyForReadAsync(
                store, passphrase, CancellationToken.None);
            using (repository)
            using (authority)
            {
                Assert.AreEqual(FixtureRepositoryV2.Repo, repository.RepositoryId);
                Assert.IsTrue(repository.KdfBelowCreationMinimums, "the fixture's KDF parameters are deliberately small");

                using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
                await reader.LoadBlobsAsync(CancellationToken.None);

                var manifestEntry = reader.AllRecords.Single(
                    record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
                var manifestRead = await reader.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);

                var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
                using var restored = new MemoryStream();
                var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
                Assert.IsTrue(restore.Success, restore.FailureDetail);
                SequenceAssert.AreEqual(FixtureRepository.FileContent(), restored.ToArray());
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
