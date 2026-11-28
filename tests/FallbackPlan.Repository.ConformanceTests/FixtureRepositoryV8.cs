using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The identity of <c>fixture-repository-v8</c>
/// ([ADR-0202](../../../docs/adr/0202-format-8-a-large-files-references-are-written-in-runs.md);
/// NFR-COMP-004): the repository of <see cref="FixtureRepositoryV7"/>,
/// written at format 8, whose tree holds one file more, a file of two
/// reference runs (06 §4.4) its manifest lists in key 15. Its blobs are
/// format 3's, stamped 3, its index format 5's, its manifests otherwise format
/// 6's and its failure format 7's. Its descriptor declares
/// <c>reference-runs</c> as required.
/// </summary>
public static class FixtureRepositoryV8
{
    /// <summary>The 200 000-byte deterministic file: concatenated SHA-256(BE64(i)).</summary>
    public static byte[] FileContent() => FixtureRepositoryBuilder.FileContent();

    /// <summary>The file of two runs: the file's first segment, 262,143 zeroes, and its second.</summary>
    public static byte[] RunsFileContent() => FixtureRepositoryBuilder.RunsFileContent();

    public const string Passphrase = "fallbackplan-fixture-v8-passphrase";

    public static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("8182838485868788898a8b8c8d8e8f90"));

    public static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    public static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x8F, 16)];
    public static readonly byte[] BackupSetId = [.. Enumerable.Repeat((byte)0x9F, 16)];
    public static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0xAF, 16)];

    public static readonly Argon2Parameters FixtureKdf = new() { MemoryKiB = 8 * 1024, Iterations = 1, Parallelism = 1 };
    public static readonly byte[] KdfSalt = [.. Enumerable.Range(0x90, 16).Select(value => (byte)value)];

    internal static FixtureRepositoryBuilder.Identity Identity { get; } = new(
        Passphrase, Repo, Writer, DeviceId, BackupSetId, SnapshotId, FixtureKdf, KdfSalt,
        FormatVersions.ReferenceRuns, "fbp-fixture-v8-spool");

    /// <summary>The fixture passphrase as a disposable value.</summary>
    public static Passphrase CreatePassphrase() => Crypto.Passphrase.Create(Passphrase);

    /// <summary>The full authority the fixture passphrase derives.</summary>
    public static RepositoryReadAuthority DeriveAuthority() => FixtureRepositoryBuilder.DeriveAuthority(Identity);

    /// <summary>Generates the complete fixture store under <paramref name="rootDirectory"/>.</summary>
    public static Task GenerateAsync(string rootDirectory, CancellationToken cancellationToken) =>
        FixtureRepositoryBuilder.GenerateAsync(Identity, rootDirectory, cancellationToken);
}
