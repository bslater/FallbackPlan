using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v2</c> (ADR-0042; NFR-COMP-004): a
/// complete, tiny write-only repository whose descriptor declares format 2
/// and the <c>sealed-data-plane</c> feature, with no key object anywhere.
/// <see cref="FixtureRepositoryBuilder"/> writes it.
/// </summary>
/// <remarks>
/// The committed copy is <b>not</b> byte-compared against a regeneration: a
/// sealed data blob takes a fresh random content key and a fresh ephemeral
/// X25519 share per seal, which is the design (ADR-0042 §2), so no two
/// generations share bytes. What the committed fixture freezes is the READ
/// contract — the bytes were written once and every future reader must keep
/// opening them: structure with the derived write bundle alone, content only
/// with the derived authority, and the wrong passphrase refused by
/// derive-and-compare.
/// <para>
/// The descriptor deliberately lists only <c>sealed-data-plane</c>, which is
/// what a repository created before the reclaim authority existed carries —
/// so the fixture is also the compatibility case for a descriptor naming
/// fewer required features than today's creator writes.
/// </para>
/// </remarks>
public static class FixtureRepositoryV2
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v2-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x55, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x66, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x77, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x20, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.SealedDataPlane, "fbp-fixture-v2-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
