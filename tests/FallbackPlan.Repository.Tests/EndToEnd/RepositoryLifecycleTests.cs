using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Create-then-open bootstrap (specification 01 §3, §6; FR-REP-002,
/// FR-ARCH-008, NFR-COMP-007): a repository created against a real store opens with the
/// right passphrase through the 01 §6 discovery order, refuses the wrong
/// passphrase indistinguishably from tampering, and surfaces the mandated
/// unstable-format warning.
/// </summary>
[TestClass]
public sealed class RepositoryLifecycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-lifecycle-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private static RepositoryCreationSettings Settings => RepositoryCreationSettings.Default with
    {
        CreatedBy = "fallbackplan-tests/1.0",
    };

    [TestMethod]
    public async Task Repository_CreatedThenOpenedWithItsPassphrase_Opens()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        Domain.Identifiers.RepositoryId created;
        using (var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None))
        {
            created = repository.RepositoryId;
            Assert.IsTrue(repository.UnstableFormatWarning, "phase-0 repositories are unstable and must say so (01 §3.2)");
        }

        using var reopened = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        Assert.AreEqual(created, reopened.RepositoryId);
        Assert.AreEqual(Domain.KeyGeneration.Zero, reopened.CurrentDataGeneration);
        Assert.AreEqual(Domain.KeyGeneration.Zero, reopened.CurrentMetadataGeneration);
        Assert.IsFalse(reopened.KdfBelowCreationMinimums);
        Assert.AreEqual("fallbackplan-tests/1.0", reopened.Descriptor.CreatedBy);
    }

    [TestMethod]
    public async Task RepositoryOpen_ThePassphraseIsWrong_FailsIndistinguishablyFromTampering()
    {
        var store = CreateStore();
        using (var passphrase = Passphrase.Create("correct horse battery staple"))
        {
            (await RepositoryLifecycle.CreateAsync(
                store, passphrase, Settings, createdAtUnixMilliseconds: 1, CancellationToken.None)).Dispose();
        }

        using var wrong = Passphrase.Create("incorrect horse battery staple");

        // 03 §3: wrong passphrase and tampered object are deliberately the
        // same failure — nothing may leak which one it was.
        await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(async () =>
            (await RepositoryLifecycle.OpenAsync(store, wrong, CancellationToken.None)).Dispose());
    }

    [TestMethod]
    public async Task RepositoryOpen_TheStoreIsEmpty_SaysItIsNotARepository()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        var exception = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None)).Dispose());

        Assert.Contains("does not hold a FallbackPlan repository", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RepositoryCreate_ARepositoryAlreadyExists_IsRefused()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        (await RepositoryLifecycle.CreateAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1, CancellationToken.None)).Dispose();

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            (await RepositoryLifecycle.CreateAsync(
                store, passphrase, Settings, createdAtUnixMilliseconds: 2, CancellationToken.None)).Dispose());
    }

    [TestMethod]
    public async Task RepositoryOpen_TheDescriptorIsCorrupted_ReportsCorruption()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        (await RepositoryLifecycle.CreateAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1, CancellationToken.None)).Dispose();

        var descriptorPath = Path.Combine(_root, "store", "repository-format");
        var bytes = await File.ReadAllBytesAsync(descriptorPath);
        bytes[20] ^= 0x01;
        await File.WriteAllBytesAsync(descriptorPath, bytes);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None)).Dispose());

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Repository_OpenedFromDisk_ArchivesAndRestores()
    {
        // The bootstrap composes with the record path: create, open, archive
        // through the opened key set, restore byte-identical.
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        using var repository = await RepositoryLifecycle.CreateAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1, CancellationToken.None);

        var data = new byte[300_000];
        new Random(17).NextBytes(data);

        var archiver = new FileArchiver(
            CapturePolicy.Default with { SegmentSize = Domain.SegmentSize.Create(64 * 1024) },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")),
            repository.CurrentDataGeneration,
            repository.Keys,
            store,
            new MonotonicBlobCounterAllocator(1),
            Path.Combine(_root, "spool"));

        using var source = new MemoryStream(data);
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
