using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v7</c>
/// ([ADR-0197](../../../docs/adr/0197-format-7-a-failure-says-what-stands-in-its-place.md);
/// NFR-COMP-004): the repository of <see cref="FixtureRepositoryV6"/>,
/// written at format 7. Its snapshot names one failure, at its file, saying
/// that what stands there is what the snapshot before held (06 §8.1 key 4),
/// and its tree still holds the file. Its blobs are format 3's, stamped 3,
/// its index format 5's, and its manifests format 6's. Its descriptor
/// declares <c>stand-in-failures</c> as required.
/// </summary>
public static class FixtureRepositoryV7
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v7-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("7172737475767778797a7b7c7d7e7f80"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x8E, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x9E, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAE, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x80, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.StandInFailures, "fbp-fixture-v7-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
