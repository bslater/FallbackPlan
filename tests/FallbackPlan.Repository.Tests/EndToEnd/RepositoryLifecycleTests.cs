using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Create-then-open bootstrap (specification 01 §3, §6; FR-REP-002,
/// FR-ARCH-008, NFR-COMP-007): a repository created against a real store opens with the
/// right passphrase through the 01 §6 discovery order — the descriptor, then
/// derive-and-compare against its sealing public key — refuses the wrong
/// passphrase by that comparison and nothing else, refuses a withdrawn format
/// by name, and surfaces the mandated unstable-format warning.
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

    private static async Task<Domain.Identifiers.RepositoryId> CreateAsync(
        LocalFileSystemObjectStore store, Passphrase passphrase, ulong createdAt = 1)
    {
        var (repository, authority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
            store, passphrase, Settings, createdAt, CancellationToken.None);
        using (repository)
        using (authority)
        {
            return repository.RepositoryId;
        }
    }

    [TestMethod]
    public async Task Repository_CreatedThenOpenedWithItsPassphrase_Opens()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        Domain.Identifiers.RepositoryId created;
        var (repository, createdAuthority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
        using (repository)
        using (createdAuthority)
        {
            created = repository.RepositoryId;
            Assert.IsTrue(repository.UnstableFormatWarning, "phase-0 repositories are unstable and must say so (01 §3.2)");
        }

        var (reopened, authority) = await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, passphrase, CancellationToken.None);
        using (reopened)
        using (authority)
        {
            Assert.AreEqual(created, reopened.RepositoryId);
            Assert.AreEqual(FormatLimits.FormatVersion, reopened.Descriptor.FormatVersion);
            Assert.AreEqual(KeyGeneration.Zero, reopened.CurrentDataGeneration);
            Assert.AreEqual(KeyGeneration.Zero, reopened.CurrentMetadataGeneration);
            Assert.IsFalse(reopened.KdfBelowCreationMinimums);
            Assert.AreEqual("fallbackplan-tests/1.0", reopened.Descriptor.CreatedBy);
            SequenceAssert.AreEqual(
                reopened.Descriptor.SealingPublicKey.ToArray(), authority.Credential.SealingPublicKey.ToArray());
        }
    }

    [TestMethod]
    public async Task RepositoryOpen_ThePassphraseIsWrong_IsRefusedByDeriveAndCompare()
    {
        var store = CreateStore();
        using (var passphrase = Passphrase.Create("correct horse battery staple"))
        {
            await CreateAsync(store, passphrase);
        }

        using var wrong = Passphrase.Create("incorrect horse battery staple");

        // 03 §9.3: equality of the derived sealing public key against the
        // descriptor's copy is the whole verifier — nothing is decrypted to
        // find out, and a wrong passphrase and an altered descriptor report
        // the same way.
        await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(async () =>
            (await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, wrong, CancellationToken.None)).Repository.Dispose());
    }

    [TestMethod]
    public async Task RepositoryOpen_TheWriteCredentialOfAnotherRepository_IsRefusedByName()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");
        await CreateAsync(store, passphrase);

        // The same passphrase under another salt is another repository's
        // credential; it is checked against the descriptor before anything
        // is read.
        var otherSalt = Enumerable.Repeat((byte)0x11, KekDerivation.SaltLength).ToArray();
        using var other = WriteOnlyDerivation.Derive(
            passphrase, Settings.KdfParameters, otherSalt, KdfValidationMode.OpenRepository);

        var refusal = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenWriteOnlyAsync(store, other.Credential, CancellationToken.None)).Dispose());
        Assert.Contains("does not belong to this repository", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RepositoryOpen_TheStoreIsEmpty_SaysItIsNotARepository()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        var exception = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, passphrase, CancellationToken.None)).Repository.Dispose());

        Assert.Contains("does not hold a FallbackPlan repository", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RepositoryCreate_ARepositoryAlreadyExists_IsRefused()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        await CreateAsync(store, passphrase);

        await Assert.ThrowsExactlyAsync<IOException>(async () => await CreateAsync(store, passphrase, createdAt: 2));
    }

    [TestMethod]
    public async Task RepositoryOpen_TheDescriptorIsCorrupted_ReportsCorruption()
    {
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        await CreateAsync(store, passphrase);

        var descriptorPath = Path.Combine(_root, "store", "repository-format");
        var bytes = await File.ReadAllBytesAsync(descriptorPath);
        bytes[20] ^= 0x01;
        await File.WriteAllBytesAsync(descriptorPath, bytes);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, passphrase, CancellationToken.None)).Repository.Dispose());

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RepositoryOpen_AFormatOneDescriptor_IsRefusedByNameNotMisread()
    {
        // Format 1 — a master key wrapped under the passphrase at /keys/ —
        // was withdrawn before any freeze (ADR-0014's rule: refuse, never
        // misread). A descriptor stamped with it is a distinct finding with
        // its remedy named, not "not a repository" and not a wrong passphrase.
        // Built by hand, because the codec no longer writes the shape.
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");
        await CreateAsync(store, passphrase);

        var descriptorPath = Path.Combine(_root, "store", "repository-format");
        var bytes = await File.ReadAllBytesAsync(descriptorPath);
        Assert.AreEqual(2, bytes[9], "the framing carries u16(2) at offset 8");
        bytes[9] = 1;
        // The body repeats the version under CBOR key 2: after the 16-byte
        // header come the map header, key 1, the byte-string header and the
        // 16-byte repository id, so key 2 sits at offset 35 and its value at 36.
        const int bodyVersion = RepositoryDescriptorCodec.HeaderLength + 1 + 1 + 1 + 16 + 1;
        Assert.AreEqual(0x02, bytes[bodyVersion - 1], "key 2");
        Assert.AreEqual(0x02, bytes[bodyVersion], "the body carries format_version 2");
        bytes[bodyVersion] = 0x01;
        // Re-stamp the trailing digest so the refusal is the version's, not the digest's.
        System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        await File.WriteAllBytesAsync(descriptorPath, bytes);

        var parsed = RepositoryDescriptorCodec.Parse(bytes);
        Assert.IsInstanceOfType<DescriptorParseResult.FormatViolation>(parsed, out var violation);
        Assert.Contains("withdrawn", violation.Message, StringComparison.Ordinal);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
            (await RepositoryLifecycle.OpenWriteOnlyForReadAsync(store, passphrase, CancellationToken.None)).Repository.Dispose());
        Assert.Contains("format 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("withdrawn", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Repository_OpenedFromDisk_ArchivesAndRestores()
    {
        // The bootstrap composes with the record path: create, open, archive
        // through the opened key set, restore byte-identical under the
        // authority the open derived.
        var store = CreateStore();
        using var passphrase = Passphrase.Create("correct horse battery staple");

        var (repository, authority) = await RepositoryLifecycle.CreateWriteOnlyAsync(
            store, passphrase, Settings, createdAtUnixMilliseconds: 1, CancellationToken.None);
        using var _repository = repository;
        using var _authority = authority;

        var data = new byte[300_000];
        new Random(17).NextBytes(data);

        var archiver = new FileArchiver(
            CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
            repository.RepositoryId,
            Domain.Identifiers.WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")),
            repository.CurrentDataGeneration,
            repository.Keys,
            store,
            new MonotonicBlobCounterAllocator(1),
            Path.Combine(_root, "spool"));

        using var source = new MemoryStream(data);
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
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
