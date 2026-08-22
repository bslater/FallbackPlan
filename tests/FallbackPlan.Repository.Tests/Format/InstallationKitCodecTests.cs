using System.Security.Cryptography;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.RecoveryKit;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Kit format v2 — the installation kit (recovery-kit spec §2.2; ADR-0013's
/// 2026-08 amendment, ADR-0044; FR-KIT-001, FR-KIT-002): eight keys, three
/// deliberate absences enforced in both directions, and a v1 kit that goes
/// on parsing exactly as it did.
/// </summary>
[TestClass]
public sealed class InstallationKitCodecTests
{
    private const string PassphraseText = "the one long passphrase of this installation";

    private static readonly byte[] Salt = Enumerable.Repeat((byte)0x37, 16).ToArray();

    private static RecoveryKit Kit()
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, Salt,
            KdfValidationMode.CreateRepository);

        return RecoveryKitFactory.BuildForInstallation(
            authority.Credential, Salt, RepositoryCreationSettings.Default.KdfParameters,
            Enumerable.Repeat((byte)0x55, 16).ToArray(), 1_722_600_000_000);
    }

    [TestMethod]
    public void InstallationKit_SerialisedAndParsed_RoundTripsTheEightKeyBody()
    {
        var original = Kit();
        var parsed = RecoveryKitCodec.Parse(RecoveryKitCodec.Serialize(original));

        Assert.AreEqual(RecoveryKit.InstallationKitVersion, parsed.KitFormatVersion);
        Assert.IsTrue(parsed.IsInstallationKit);
        SequenceAssert.AreEqual(Salt, parsed.KdfSalt.ToArray());
        SequenceAssert.AreEqual(original.SealingPublicKey.ToArray(), parsed.SealingPublicKey.ToArray());
        Assert.AreEqual(original.Instructions, parsed.Instructions);
        Assert.AreEqual(original.IssuedAt, parsed.IssuedAt);
    }

    [TestMethod]
    public void InstallationKit_TheThreeAbsences_SurviveTheRoundTrip()
    {
        // Each absence is a decision, not an economy: no repository id
        // because one kit opens every archive; no key object because a
        // write-only repository has none; no destinations because the kit
        // predates every destination the installation will ever have.
        var parsed = RecoveryKitCodec.Parse(RecoveryKitCodec.Serialize(Kit()));

        Assert.IsNull(parsed.RepositoryId, "an installation kit names no repository");
        Assert.IsTrue(parsed.KeyObject.IsEmpty, "an installation kit carries no key object");
        Assert.IsEmpty(parsed.Destinations, "an installation kit lists no destination");
    }

    [TestMethod]
    public void InstallationKit_CarriesNoKeyMaterial_OnlyThePublicVerifier()
    {
        // FR-KIT-002 at the bytes. The framed kit is what a person prints
        // and leaves in a drawer; anything derived that is not public
        // appearing in it would be the whole design failing at once.
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, Salt,
            KdfValidationMode.CreateRepository);

        var framed = RecoveryKitCodec.Serialize(RecoveryKitFactory.BuildForInstallation(
            authority.Credential, Salt, RepositoryCreationSettings.Default.KdfParameters,
            Enumerable.Repeat((byte)0x55, 16).ToArray(), 1_722_600_000_000));

        Assert.IsFalse(
            Contains(framed, System.Text.Encoding.UTF8.GetBytes(PassphraseText)),
            "the passphrase must not appear in the kit");
        Assert.IsFalse(
            Contains(framed, authority.SealingPrivateKey.ToArray()),
            "the sealing PRIVATE key must not appear in the kit");
        Assert.IsFalse(
            Contains(framed, authority.Credential.ContentIdKey.ToArray()),
            "the content-id key must not appear in the kit");
        Assert.IsFalse(
            Contains(framed, authority.Credential.DeriveMetadataKey(FallbackPlan.Domain.KeyGeneration.Zero)),
            "the metadata key must not appear in the kit");

        Assert.IsTrue(
            Contains(framed, authority.Credential.SealingPublicKey.ToArray()),
            "the public verifier is the one key-shaped thing that belongs here");
    }

    [TestMethod]
    public void InstallationKit_ClaimingARepositoryId_IsRefused()
    {
        // A v2 kit that names a repository is either a forgery or a v1 kit
        // with its version stamped over. Either way it would open one
        // archive and silently fail the rest.
        var smuggled = Kit() with
        {
            RepositoryId = RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20")),
        };

        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(smuggled));
    }

    [TestMethod]
    public void InstallationKit_ClaimingAKeyObjectOrADestination_IsRefused()
    {
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            Kit() with { KeyObject = Enumerable.Repeat((byte)0x01, 64).ToArray() }));

        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            Kit() with { Destinations = [new KitDestination("local-path", "file:///x", "", "")] }));
    }

    [TestMethod]
    public void InstallationKit_WithoutTheSealingKey_IsRefused()
    {
        // Without the verifier there is nothing to prove a derivation
        // against, and "the passphrase is wrong" becomes indistinguishable
        // from "the archive is not yours".
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            Kit() with { SealingPublicKey = ReadOnlyMemory<byte>.Empty }));
    }

    [TestMethod]
    public void InstallationKit_ADamagedChecksum_IsReportedAsDamage()
    {
        var framed = RecoveryKitCodec.Serialize(Kit());
        framed[^1] ^= 0xFF;

        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Parse(framed));
    }

    [TestMethod]
    public void InstallationKit_TheTextForm_CarriesIdenticalContent()
    {
        // FR-KIT-003's machine/printable equivalence: the text form is a
        // rendering of the framed binary, so transcribing it back must yield
        // the same bytes and therefore the same kit.
        var framed = RecoveryKitCodec.Serialize(Kit());
        var text = RecoveryKitText.Render(framed, "one factor — store it apart from the passphrase");

        SequenceAssert.AreEqual(framed, RecoveryKitText.ParseToFramed(text));
        Assert.AreEqual(
            RecoveryKitCodec.Parse(framed).IssuedAt,
            RecoveryKitCodec.Parse(RecoveryKitText.ParseToFramed(text)).IssuedAt);
    }

    [TestMethod]
    public void RepositoryKit_IsUnaffected_AndStillParses()
    {
        // The whole point of a versioned format: v1 kits in a drawer keep
        // working, and a tool that reads v2 still reads them.
        var v1 = new RecoveryKit
        {
            KitFormatVersion = 1,
            MinimumToolVersion = "0.1.0",
            RepositoryId = RepositoryId.FromBytes(Convert.FromHexString("1112131415161718191a1b1c1d1e1f20")),
            RepositoryFormatVersion = FallbackPlan.Domain.FormatLimits.SealedFormatVersion,
            KeyObject = ReadOnlyMemory<byte>.Empty,
            KdfMemoryKiB = 8 * 1024,
            KdfIterations = 1,
            KdfParallelism = 1,
            KdfSalt = Enumerable.Repeat((byte)0x21, 16).ToArray(),
            Destinations = [new KitDestination("local-path", "file:///drill", "", "")],
            IssuingDeviceId = Enumerable.Repeat((byte)0x55, 16).ToArray(),
            IssuedAt = 1_722_600_000_000,
            Instructions = "derive, compare, restore",
            SealingPublicKey = Enumerable.Repeat((byte)0x9C, 32).ToArray(),
        };

        var parsed = RecoveryKitCodec.Parse(RecoveryKitCodec.Serialize(v1));

        Assert.IsFalse(parsed.IsInstallationKit);
        Assert.IsNotNull(parsed.RepositoryId);
        Assert.ContainsSingle(parsed.Destinations);
    }

    [TestMethod]
    public void RepositoryKit_WithoutARepositoryId_IsRefused()
    {
        Assert.ThrowsExactly<RecoveryKitFormatException>(() => RecoveryKitCodec.Serialize(
            new RecoveryKit
            {
                KitFormatVersion = 1,
                MinimumToolVersion = "0.1.0",
                RepositoryId = null,
                RepositoryFormatVersion = 1,
                KeyObject = Enumerable.Repeat((byte)0x01, 64).ToArray(),
                KdfMemoryKiB = 8 * 1024,
                KdfIterations = 1,
                KdfParallelism = 1,
                KdfSalt = Enumerable.Repeat((byte)0x21, 16).ToArray(),
                IssuingDeviceId = Enumerable.Repeat((byte)0x55, 16).ToArray(),
                IssuedAt = 1,
                Instructions = "x",
            }));
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        needle.Length > 0 && haystack.IndexOf(needle) >= 0;
}
