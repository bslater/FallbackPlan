using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The recovery kit against its specification (specifications/recovery-kit;
/// ADR-0013; FR-KIT-001..003): every vector case round-trips or refuses as
/// committed, the text form is canonical to the character, a transcription
/// error is caught by its line, and the committed fixture kit — regenerated
/// byte-identically — opens the fixture repository with nothing but the
/// passphrase.
/// </summary>
[TestClass]
public sealed class RecoveryKitConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "recovery-kit.json")));

    private static JsonElement Kit => Vectors.RootElement.GetProperty("kit");

    [TestMethod]
    public void RecoveryKit_TheVectorKit_ParsesWithEveryCommittedField()
    {
        var framed = Convert.FromHexString(Kit.GetProperty("framed_hex").GetString()!);
        var kit = RecoveryKitCodec.Parse(framed);
        var fields = Kit.GetProperty("fields");

        Assert.AreEqual(fields.GetProperty("kit_format_version").GetUInt16(), kit.KitFormatVersion);
        Assert.AreEqual(fields.GetProperty("minimum_tool_version").GetString(), kit.MinimumToolVersion);
        SequenceAssert.AreEqual(
            fields.GetProperty("repository_id").GetString(),
            Convert.ToHexString(kit.RepositoryId.ToArray()).ToLowerInvariant());
        Assert.AreEqual(fields.GetProperty("issued_at").GetUInt64(), kit.IssuedAt);
        Assert.AreEqual(fields.GetProperty("destination_count").GetInt32(), kit.Destinations.Count);
        Assert.AreEqual(fields.GetProperty("kdf").GetProperty("memory_kib").GetUInt32(), kit.KdfMemoryKiB);
    }

    [TestMethod]
    public void RecoveryKitText_AnyKit_RendersCanonicallyAndParsesBack()
    {
        var framed = Convert.FromHexString(Kit.GetProperty("framed_hex").GetString()!);

        // The committed text is the canonical layout minus our instruction
        // header — compare payload lines exactly, then round-trip.
        var expected = Kit.GetProperty("text_form").GetString()!;
        var rendered = RecoveryKitText.Render(framed);
        SequenceAssert.AreEqual(
            expected.Split('\n').Where(line => line.Length > 2 && char.IsAsciiDigit(line[0])),
            rendered.Split('\n').Where(line => line.Length > 2 && char.IsAsciiDigit(line[0])));

        SequenceAssert.AreEqual(framed, RecoveryKitText.ParseToFramed(expected));
        SequenceAssert.AreEqual(framed, RecoveryKitText.ParseToFramed(rendered));
    }

    [TestMethod]
    public void RecoveryKitText_ATranscriptionError_IsCaughtAndItsLineNamed()
    {
        var damaged = Kit.GetProperty("damaged_text_form").GetString()!;

        var exception = Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitText.ParseToFramed(damaged));
        Assert.Contains("Line 02", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void WriteOnlyKit_TheCommittedVector_ParsesEmptyHandedWithTheSealingKey()
    {
        // The eleven-key shape (ADR-0042; recovery-kit section 2.1): format
        // version 2, key 5 EMPTY — the kit carries no key material at all —
        // and key 11 the sealing public key the ceremony compares against.
        var vector = Vectors.RootElement.GetProperty("write_only_kit");
        var kit = RecoveryKitCodec.Parse(Convert.FromHexString(vector.GetProperty("framed_hex").GetString()!));
        var fields = vector.GetProperty("fields");

        Assert.AreEqual(
            fields.GetProperty("repository_format_version").GetUInt16(), kit.RepositoryFormatVersion);
        Assert.IsTrue(kit.KeyObject.IsEmpty, "a write-only kit carries no key material");
        Assert.AreEqual(
            fields.GetProperty("sealing_public_key").GetString(),
            Convert.ToHexStringLower(kit.SealingPublicKey.ToArray()));
    }

    [TestMethod]
    public void RecoveryKit_EveryCommittedRefusalCase_IsRefused()
    {
        foreach (var refusal in Vectors.RootElement.GetProperty("refusal_cases").EnumerateArray())
        {
            var framed = Convert.FromHexString(refusal.GetProperty("framed_hex").GetString()!);

            Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Parse(framed));
        }
    }

    // ------------------------------------------------------- fixture kit

    private static string FixtureKitDirectory([CallerFilePath] string sourceFile = "")
    {
        // The binary's location anchors first: ContinuousIntegrationBuild
        // maps [CallerFilePath] to /_/…, which does not exist on a CI
        // runner; the source path remains the fallback.
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v1-kit");
    }

    private static string? LocateRoot(string? start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    private static string FixtureStorePath(string kitDirectory) =>
        Path.Combine(Path.GetDirectoryName(kitDirectory)!, "fixture-repository-v1");

    [TestMethod]
    public async Task RecoveryKit_TheCommittedFixture_IsByteIdenticalToRegeneration()
    {
        var directory = FixtureKitDirectory();
        var binPath = Path.Combine(directory, "kit.bin");
        var textPath = Path.Combine(directory, "kit.txt");

        if (!File.Exists(binPath))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(binPath, FixtureRepository.BuildKitFramed());
            await File.WriteAllTextAsync(textPath, FixtureRepository.BuildKitText());
            Assert.Fail($"The committed fixture kit was absent and has been generated at {directory} — review and commit it.");
        }

        SequenceAssert.AreEqual(await File.ReadAllBytesAsync(binPath), FixtureRepository.BuildKitFramed());
        Assert.AreEqual(await File.ReadAllTextAsync(textPath), FixtureRepository.BuildKitText());
    }

    [TestMethod]
    public async Task RecoveryKit_TheFixtureKitAndItsPassphrase_OpenTheFixtureRepository()
    {
        // FR-KIT-001, the clean-machine premise: no catalogue, no state
        // directory, no /keys/ listing — the kit supplies the key object and
        // KDF parameters; the store supplies everything else.
        var kit = RecoveryKitCodec.Parse(
            await File.ReadAllBytesAsync(Path.Combine(FixtureKitDirectory(), "kit.bin")));

        using var passphrase = FixtureRepository.CreatePassphrase();
        using var derivation = KekDerivation.Derive(
            passphrase,
            new Argon2Parameters
            {
                MemoryKiB = kit.KdfMemoryKiB,
                Iterations = kit.KdfIterations,
                Parallelism = kit.KdfParallelism,
            },
            kit.KdfSalt.Span,
            KdfValidationMode.OpenRepository);

        var keyObject = KeyObjectFraming.Parse(kit.KeyObject.Span);
        var bundleCbor = KeyWrapping.Unwrap(
            derivation.Kek,
            keyObject.WrapNonce,
            KeyObjectFraming.BuildAad(keyObject.FormatVersion, keyObject.KekProfile, keyObject.KeyId),
            keyObject.Wrapped,
            keyObject.Tag);

        try
        {
            using var bundle = KeyBundleCodec.Decode(bundleCbor);
            SequenceAssert.AreEqual(FixtureRepository.MasterKey, bundle.MasterKey.ToArray());

            // With the unwrapped keys, restore the fixture file through the
            // ordinary read path — the kit was the only key source.
            using var keys = RepositoryKeySet.FromMasterKey(bundle.MasterKey);
            var store = new LocalFileSystemObjectStore(FixtureStorePath(FixtureKitDirectory()));

            using var reader = new RepositoryReader(kit.RepositoryId, keys, store);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var manifestEntry = reader.AllRecords.Single(
                record => record.ObjectType == Domain.ObjectType.FileVersionManifest);
            var read = await reader.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);

            var manifest = Format.Manifests.FileVersionManifestCodec.Decode(read.Plaintext!);
            using var restored = new MemoryStream();
            var result = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);

            Assert.IsTrue(result.Success, result.FailureDetail);
            SequenceAssert.AreEqual(FixtureRepository.FileContent(), restored.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundleCbor);
        }
    }

    [TestMethod]
    public async Task RecoveryKitText_TheFixtureKit_RoundTripsToTheBinaryForm()
    {
        var directory = FixtureKitDirectory();
        var framed = await File.ReadAllBytesAsync(Path.Combine(directory, "kit.bin"));
        var text = await File.ReadAllTextAsync(Path.Combine(directory, "kit.txt"));

        SequenceAssert.AreEqual(framed, RecoveryKitText.ParseToFramed(text));
    }
}
