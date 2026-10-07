using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The envelope an S3-compatible destination's secret access key crosses the
/// command surface in (ADR-0091, NFR-SEC-009): sealed end-to-end to the
/// service's recipient key where it was typed, bound to the destination and
/// the access key id it was typed for, under a purpose of its own so it can
/// be opened as nothing else and nothing else can be opened as it.
/// </summary>
[TestClass]
public sealed class AccessKeyEnvelopeTests
{
    private static readonly byte[] RecipientPrivate = [.. Enumerable.Repeat((byte)0x5A, 32)];

    private static readonly byte[] RecipientPublic = ContentSealing.PublicKeyOf(RecipientPrivate);

    private const string Secret = "fbp/envelope+secret=key/0123456789abcdef";

    [TestMethod]
    public void AccessKeyEnvelope_OpenedForTheDestinationItWasSealedFor_GivesTheSecretBack()
    {
        var sealedBytes = WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", "FBPKEYID0001", Secret);

        Assert.AreEqual(
            Secret, WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, sealedBytes, "cloud", "FBPKEYID0001"));
        Assert.DoesNotContain(
            "envelope+secret", System.Text.Encoding.Latin1.GetString(sealedBytes), StringComparison.Ordinal);
    }

    [TestMethod]
    public void AccessKeyEnvelope_OpenedForAnotherDestinationOrKeyId_IsRefused()
    {
        // Bound to what it was typed for: an envelope sealed for one
        // destination cannot be replayed into another's credentials, nor
        // stored beside a key id it was not issued with.
        var sealedBytes = WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", "FBPKEYID0001", Secret);

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, sealedBytes, "offsite", "FBPKEYID0001"));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, sealedBytes, "cloud", "FBPKEYID0002"));
    }

    [TestMethod]
    public void AccessKeyEnvelope_AndTheOtherEnvelopes_CannotBeOpenedAsEachOther()
    {
        // Each envelope has a purpose of its own (ADR-0042 §4): a restore
        // grant is not a secret access key, and a secret access key is not a
        // grant, whatever their payloads happen to look like.
        var grant = WriteOnlyProvisioning.SealGrant(RecipientPublic, new byte[WriteOnlyDerivation.SealingKeyLength]);
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, grant, "cloud", "FBPKEYID0001"));

        var accessKey = WriteOnlyProvisioning.SealAccessKeySecret(
            RecipientPublic, "cloud", "FBPKEYID0001", new string('k', WriteOnlyDerivation.SealingKeyLength));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenGrant(RecipientPrivate, accessKey));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenProvision(RecipientPrivate, accessKey));
    }

    [TestMethod]
    public void AccessKeyEnvelope_ATamperedOrMisaddressedEnvelope_IsRefusedIndistinguishably()
    {
        var sealedBytes = WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", "FBPKEYID0001", Secret);
        var tampered = sealedBytes.ToArray();
        tampered[^1] ^= 0x01;
        var otherRecipient = Enumerable.Repeat((byte)0x5B, 32).ToArray();

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, tampered, "cloud", "FBPKEYID0001"));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(otherRecipient, sealedBytes, "cloud", "FBPKEYID0001"));
    }

    [TestMethod]
    public void AccessKeyEnvelope_ASecretThatCannotBeOne_IsRefusedBeforeSealing()
    {
        // Nothing is sealed that the store could never accept: empty, a
        // control character (a pasted line break), or longer than any
        // provider issues.
        foreach (var malformed in new[] { string.Empty, "line\nbreak", new string('x', 257) })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", "FBPKEYID0001", malformed));
        }

        Assert.ThrowsExactly<ArgumentException>(
            () => WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", " ", Secret));
        Assert.ThrowsExactly<ArgumentException>(
            () => WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, " ", "FBPKEYID0001", Secret));
    }
}
