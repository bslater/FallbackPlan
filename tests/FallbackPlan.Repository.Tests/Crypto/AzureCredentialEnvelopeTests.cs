using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The envelopes an Azure Blob destination's credential crosses the command
/// surface in (ADR-0093, NFR-SEC-009): an account key or a shared access
/// signature, sealed end-to-end to the service's recipient key where it was
/// typed, bound to the destination it was typed for, each under a purpose of
/// its own — so neither can be opened as the other, as an S3-compatible
/// store's secret access key, or as any other envelope.
/// </summary>
[TestClass]
public sealed class AzureCredentialEnvelopeTests
{
    private static readonly byte[] RecipientPrivate = [.. Enumerable.Repeat((byte)0x5A, 32)];

    private static readonly byte[] RecipientPublic = ContentSealing.PublicKeyOf(RecipientPrivate);

    private const string AccountKey =
        "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRg==";

    private const string Token = "sv=2024-11-04&sr=c&sp=racwdl&se=2026-12-31T00%3A00%3A00Z&spr=https&sig=AbC%2Bd%2Fe%3D";

    [TestMethod]
    public void AccountKeyEnvelope_OpenedForTheDestinationItWasSealedFor_GivesTheKeyBack()
    {
        var sealedBytes = WriteOnlyProvisioning.SealAccountKey(RecipientPublic, "cloud", AccountKey);

        Assert.AreEqual(AccountKey, WriteOnlyProvisioning.OpenAccountKey(RecipientPrivate, sealedBytes, "cloud"));
        Assert.DoesNotContain("BwgJCgsM", System.Text.Encoding.Latin1.GetString(sealedBytes), StringComparison.Ordinal);
    }

    [TestMethod]
    public void SharedAccessSignatureEnvelope_OpenedForTheDestinationItWasSealedFor_GivesTheTokenBack()
    {
        // A token can run far longer than an access key: one that names a
        // delegated key or a stored policy carries a dozen fields.
        var longToken = Token + "&skoid=" + new string('a', 36) + "&sktid=" + new string('b', 36) + "&x=" + new string('c', 700);

        var sealedBytes = WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, "cloud", longToken);

        Assert.AreEqual(longToken, WriteOnlyProvisioning.OpenSharedAccessSignature(RecipientPrivate, sealedBytes, "cloud"));
        Assert.DoesNotContain("sig=", System.Text.Encoding.Latin1.GetString(sealedBytes), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Envelopes_OpenedForAnotherDestination_AreRefused()
    {
        // Bound to what it was typed for: an envelope sealed for one
        // destination cannot be replayed into another's credentials.
        var key = WriteOnlyProvisioning.SealAccountKey(RecipientPublic, "cloud", AccountKey);
        var token = WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, "cloud", Token);

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccountKey(RecipientPrivate, key, "offsite"));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenSharedAccessSignature(RecipientPrivate, token, "offsite"));
    }

    [TestMethod]
    public void Envelopes_OfEachCredentialKind_CannotBeOpenedAsAnother()
    {
        // Each kind has a purpose of its own (ADR-0042 §4): an account key
        // signs every request, a token is carried in one, and a secret access
        // key belongs to another API altogether. None is the other, whatever
        // its payload looks like.
        var key = WriteOnlyProvisioning.SealAccountKey(RecipientPublic, "cloud", AccountKey);
        var token = WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, "cloud", AccountKey);
        var accessKey = WriteOnlyProvisioning.SealAccessKeySecret(RecipientPublic, "cloud", "FBPKEYID0001", AccountKey);

        Assert.ThrowsExactly<SealedContentException>(() => WriteOnlyProvisioning.OpenSharedAccessSignature(RecipientPrivate, key, "cloud"));
        Assert.ThrowsExactly<SealedContentException>(() => WriteOnlyProvisioning.OpenAccountKey(RecipientPrivate, token, "cloud"));
        Assert.ThrowsExactly<SealedContentException>(() => WriteOnlyProvisioning.OpenAccountKey(RecipientPrivate, accessKey, "cloud"));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenAccessKeySecret(RecipientPrivate, key, "cloud", "FBPKEYID0001"));
        Assert.ThrowsExactly<SealedContentException>(() => WriteOnlyProvisioning.OpenGrant(RecipientPrivate, key));
        Assert.ThrowsExactly<SealedContentException>(() => WriteOnlyProvisioning.OpenProvision(RecipientPrivate, token));
    }

    [TestMethod]
    public void Envelopes_ATamperedOrMisaddressedEnvelope_IsRefusedIndistinguishably()
    {
        var sealedBytes = WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, "cloud", Token);
        var tampered = sealedBytes.ToArray();
        tampered[^1] ^= 0x01;
        var otherRecipient = Enumerable.Repeat((byte)0x5B, 32).ToArray();

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenSharedAccessSignature(RecipientPrivate, tampered, "cloud"));
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenSharedAccessSignature(otherRecipient, sealedBytes, "cloud"));
    }

    [TestMethod]
    public void Envelopes_ASecretThatCannotBeOne_IsRefusedBeforeSealing()
    {
        // Nothing is sealed that the store could never accept: empty, a
        // control character (a pasted line break), or longer than any
        // provider issues.
        foreach (var malformed in new[] { string.Empty, "line\nbreak", new string('x', WriteOnlyProvisioning.MaximumAccountKeyLength + 1) })
        {
            Assert.ThrowsExactly<ArgumentException>(() => WriteOnlyProvisioning.SealAccountKey(RecipientPublic, "cloud", malformed));
        }

        foreach (var malformed in new[] { string.Empty, "sv=1&sig=a\r\n", new string('x', WriteOnlyProvisioning.MaximumSharedAccessSignatureLength + 1) })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, "cloud", malformed));
        }

        Assert.ThrowsExactly<ArgumentException>(() => WriteOnlyProvisioning.SealAccountKey(RecipientPublic, " ", AccountKey));
        Assert.ThrowsExactly<ArgumentException>(() => WriteOnlyProvisioning.SealSharedAccessSignature(RecipientPublic, " ", Token));
        Assert.AreEqual(2048, WriteOnlyProvisioning.MaximumSharedAccessSignatureLength);
    }
}
