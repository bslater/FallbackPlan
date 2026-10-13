using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v4</c>
/// ([ADR-0096](../../../docs/adr/0096-format-4-an-index-generation-per-delta.md);
/// NFR-COMP-004): the same six-object repository as
/// <see cref="FixtureRepositoryV3"/>, written at format 4. Its records and
/// blobs are format 3's, stamped 3. Its one delta carries key 12, index
/// generation 1, and its descriptor declares <c>index-generations</c> as
/// required.
/// </summary>
public static class FixtureRepositoryV4
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v4-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("4142434445464748494a4b4c4d4e4f50"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("e0e1e2e3e4e5e6e7e8e9eaebecedeeef"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x8B, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x9B, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAB, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x50, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.IndexGenerations, "fbp-fixture-v4-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
