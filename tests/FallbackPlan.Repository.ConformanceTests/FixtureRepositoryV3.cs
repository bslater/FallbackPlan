using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v3</c>
/// ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1; NFR-COMP-004): the same six-object repository as
/// <see cref="FixtureRepositoryV2"/>, written at format 3 — metadata blobs
/// stamped 3, a data blob whose envelope carries no share because every
/// record carries its own, and a descriptor declaring
/// <c>relocatable-records</c> as required.
/// </summary>
/// <remarks>
/// Byte-reproducibility is further out of reach here than for format 2, not
/// closer: a format-3 record draws a fresh nonce and a fresh ephemeral share
/// each. The committed bytes freeze the read contract, and one property that
/// only frozen bytes can establish — that a data record lifted out of this
/// blob opens in a blob written later, by a different writer, at a different
/// ordinal.
/// </remarks>
public static class FixtureRepositoryV3
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v3-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("3132333435363738393a3b3c3d3e3f40"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x88, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x99, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAA, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x40, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.RelocatableRecords, "fbp-fixture-v3-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
