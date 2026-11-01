using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v6</c>
/// ([ADR-0184](../../../docs/adr/0184-format-6-a-manifest-keeps-a-files-flags-and-a-links-form.md);
/// NFR-COMP-004): the same seven-object repository as
/// <see cref="FixtureRepositoryV5"/>, written at format 6. Its file's
/// manifest keeps the no-dump flag (06 §4.1 key 11), and its tree holds a
/// second entry, a junction, whose manifest names it a mount point (06 §4 key
/// 14). Its blobs are format 3's, stamped 3, and its index format 5's. Its
/// descriptor declares <c>file-flags-and-link-forms</c> as required.
/// </summary>
public static class FixtureRepositoryV6
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    public const string Passphrase = "fallbackplan-fixture-v6-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("6162636465666768696a6b6c6d6e6f70"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x8D, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x9D, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAD, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x70, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.FileFlagsAndLinkForms, "fbp-fixture-v6-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
