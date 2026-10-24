using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v5</c>
/// ([ADR-0097](../../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md);
/// NFR-COMP-004): the same six-object repository as
/// <see cref="FixtureRepositoryV4"/>, written at format 5, plus a seventh
/// object: a checkpoint carrying the one delta's sealed bytes. Its records
/// and blobs are format 3's, stamped 3; its delta is format 4's. Its
/// descriptor declares <c>delta-checkpoints</c> as required.
/// </summary>
public static class FixtureRepositoryV5
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v5-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("5152535455565758595a5b5c5d5e5f60"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x8C, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x9C, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAC, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x60, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.DeltaCheckpoints, "fbp-fixture-v5-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
