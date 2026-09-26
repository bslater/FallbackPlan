using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The effective format version (specification 11 §5): the descriptor says
/// what a repository was created at, and an append-only signed upgrade record
/// says what it writes now. The descriptor is never rewritten — no
/// destination would accept a replacement — so the two are read together, and
/// a record nobody with the signing key authored is ignored rather than
/// obeyed.
/// </summary>
/// <remarks>Establishes FR-MAN-019 and NFR-COMP-004.</remarks>
[TestClass]
public sealed class EffectiveFormatTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-effective-format", Guid.NewGuid().ToString("n"));

    private static readonly byte[] WriterId = [.. Enumerable.Repeat((byte)11, 16)];

    private LocalFileSystemObjectStore CreateStore(string name) => new(Path.Combine(_root, name));

    private static async Task<(LocalFileSystemObjectStore Store, OpenedRepository Repository, RepositoryReadAuthority Authority)>
        CreateAsync(LocalFileSystemObjectStore store, ushort formatVersion)
    {
        using var passphrase = Passphrase.Create("correct horse battery staple");
        var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
            store,
            passphrase,
            RepositoryCreationSettings.Default with
            {
                CreatedBy = "fallbackplan-tests/1.0",
                FormatVersion = formatVersion,
            },
            createdAtUnixMilliseconds: 1,
            CancellationToken.None);

        return (store, repository, authority);
    }

    [TestMethod]
    public async Task EffectiveFormat_WithNoUpgradeRecord_IsTheDescriptorsOwnVersion()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("plain"), FormatVersions.SealedDataPlane);
        using (repository)
        using (authority)
        {
            var effective = await RepositoryLifecycle.ReadEffectiveFormatAsync(
                store, repository.Descriptor, repository.Credential, CancellationToken.None);

            Assert.AreEqual(FormatVersions.SealedDataPlane, effective);
        }
    }

    [TestMethod]
    public async Task EffectiveFormat_AfterAnUpgradeIsWritten_IsTheVersionTheRecordNames()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("upgraded"), FormatVersions.SealedDataPlane);
        using (repository)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store,
                repository.Descriptor,
                repository.Credential,
                FormatVersions.RelocatableRecords,
                WriterId,
                upgradedAtUnixMilliseconds: 1_700_000_000_000,
                CancellationToken.None);

            var effective = await RepositoryLifecycle.ReadEffectiveFormatAsync(
                store, repository.Descriptor, repository.Credential, CancellationToken.None);

            Assert.AreEqual(FormatVersions.RelocatableRecords, effective);

            // The descriptor is untouched: it is what every destination and
            // every peer replica already holds, and an upgrade that rewrote
            // it would move the source alone.
            var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
            Assert.AreEqual(FormatVersions.SealedDataPlane, descriptor.FormatVersion);
        }
    }

    [TestMethod]
    public async Task EffectiveFormat_ARecordSignedByAnotherKey_IsIgnored()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("stranger"), FormatVersions.SealedDataPlane);
        using (repository)
        using (authority)
        {
            // A stranger's file is a claim nobody made. Obeying it would let
            // anyone who can write into the archive move the format; refusing
            // to open over it would let them deny service. Ignored is the
            // only answer that is neither.
            var record = new FormatUpgradeRecord(
                FormatVersions.SealedDataPlane, FormatVersions.RelocatableRecords, 1_700_000_000_000, WriterId);
            var stranger = RepositorySigner.FromSeed([.. Enumerable.Repeat((byte)42, 32)], KeyGeneration.Zero);
            var signature = stranger.Sign(FormatUpgradeRecordCodec.EncodeForSigning(record));
            stranger.Dispose();

            await WriteRawAsync(store, record.ToVersion, FormatUpgradeRecordCodec.Encode(record, signature));

            var effective = await RepositoryLifecycle.ReadEffectiveFormatAsync(
                store, repository.Descriptor, repository.Credential, CancellationToken.None);

            Assert.AreEqual(FormatVersions.SealedDataPlane, effective);
        }
    }

    [TestMethod]
    public async Task EffectiveFormat_AnUnreadableRecord_IsIgnoredAndTheOthersStillCount()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("damaged"), FormatVersions.SealedDataPlane);
        using (repository)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, repository.Descriptor, repository.Credential, FormatVersions.RelocatableRecords,
                WriterId, 1_700_000_000_000, CancellationToken.None);
            await WriteRawAsync(store, 4, [0x01, 0x02, 0x03]);

            var effective = await RepositoryLifecycle.ReadEffectiveFormatAsync(
                store, repository.Descriptor, repository.Credential, CancellationToken.None);

            Assert.AreEqual(FormatVersions.RelocatableRecords, effective);
        }
    }

    [TestMethod]
    public async Task EffectiveFormat_ARecordBelowTheDescriptorsVersion_NeverMovesItBackwards()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("backwards"), FormatVersions.RelocatableRecords);
        using (repository)
        using (authority)
        {
            // A repository created at 3 carrying a valid 1→2 record is still
            // at 3. The rule is the highest valid claim at or above the
            // descriptor, never the newest file written.
            var record = new FormatUpgradeRecord(
                FormatVersions.Symmetric, FormatVersions.SealedDataPlane, 1_700_000_000_000, WriterId);
            using var signer = RepositorySigner.Create(repository.Credential, KeyGeneration.Zero);
            var signature = signer.Sign(FormatUpgradeRecordCodec.EncodeForSigning(record));

            await WriteRawAsync(store, record.ToVersion, FormatUpgradeRecordCodec.Encode(record, signature));

            var effective = await RepositoryLifecycle.ReadEffectiveFormatAsync(
                store, repository.Descriptor, repository.Credential, CancellationToken.None);

            Assert.AreEqual(FormatVersions.RelocatableRecords, effective);
        }
    }

    [TestMethod]
    public async Task WriteFormatUpgrade_ToAVersionTheRepositoryIsAlreadyAt_IsRefusedByName()
    {
        var (store, repository, authority) = await CreateAsync(CreateStore("already"), FormatVersions.RelocatableRecords);
        using (repository)
        using (authority)
        {
            var refusal = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await RepositoryLifecycle.WriteFormatUpgradeAsync(
                    store, repository.Descriptor, repository.Credential, FormatVersions.RelocatableRecords,
                    WriterId, 1_700_000_000_000, CancellationToken.None));

            Assert.Contains("already", refusal.Message, StringComparison.Ordinal);
        }
    }

    private static async Task WriteRawAsync(LocalFileSystemObjectStore store, ushort toVersion, byte[] content)
    {
        var result = await store.PutAsync(
            ObjectKey.Parse(FormatUpgradeRecordCodec.KeyFor(toVersion)),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)),
            PutConditions.IfNotExists,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, result.Outcome);
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
