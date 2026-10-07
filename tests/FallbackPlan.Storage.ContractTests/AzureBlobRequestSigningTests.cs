using FallbackPlan.Storage.AzureBlob;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The Azure Blob provider signs every request as the Blob service's Shared
/// Key scheme specifies it, or carries a shared access signature in its
/// query as issued (FR-REP-002, ADR-0093). Each case is one shape of request
/// the provider sends, and each answer was computed for exactly that request
/// by the API's reference client library's own Shared Key policy, given the
/// same headers — so a signer that agrees with it agrees with every store
/// the library is used against, and one that drifts fails here rather than
/// at a store that refuses every request.
/// </summary>
[TestClass]
public sealed class AzureBlobRequestSigningTests
{
    private const string Account = "fbptestaccount";

    private static readonly AzureBlobCredentials Key = AzureBlobCredentials.SharedKey(
        "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRg==");

    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Sign_ARangedReadOfAHostAddressedAccount_MatchesTheReferenceSignature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://fbptestaccount.blob.example.net/family-backups/site-a/repo/blobs/data/ab/key");
        request.Headers.TryAddWithoutValidation("x-ms-range", "bytes=0-1023");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:SppA7BMVHBlXdwxCq2hy4tTpEK+LEfqiIWLJYQ+82dA=", AuthorizationOf(request));
        Assert.AreEqual("Tue, 06 Oct 2026 12:00:00 GMT", Assert.ContainsSingle(request.Headers.GetValues("x-ms-date")));
        Assert.AreEqual(AzureBlobRequestSigner.ApiVersion, Assert.ContainsSingle(request.Headers.GetValues("x-ms-version")));
        Assert.AreEqual("2024-11-04", AzureBlobRequestSigner.ApiVersion);
    }

    [TestMethod]
    public void Sign_ACreateOnlyPut_SignsTheConditionTheLengthAndTheDigest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, "https://fbptestaccount.blob.example.net/family-backups/site-a/repo/journal/w/0001")
        {
            Content = new ByteArrayContent("sealed blob"u8.ToArray()),
        };
        request.Content.Headers.ContentLength = 11;
        request.Content.Headers.ContentMD5 = Convert.FromBase64String("8mR1IrDWoUvZQFIY5DmM5Q==");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:qkdg8Yjqj6QEWZOiNrffYEwP2E7nHvth3kHvAFn8/4I=", AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_AnEmptyPut_SignsNoLength_AsTheSchemeRequires()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, "https://fbptestaccount.blob.example.net/family-backups/site-a/repo/locks/empty")
        {
            Content = new ByteArrayContent([]),
        };
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.ContentMD5 = Convert.FromBase64String("1B2M2Y8AsgTpgAmY7PhCfg==");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:/14s/lVOli4oAAAz6AXAZaHI2YypTNAIfeyzT6J/u7w=", AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_AHeadOnAPortOfThisMachine_NamesTheAccountInThePathItSigns()
    {
        // Path-style, as a store on this machine is addressed: the account is
        // the path's first segment, and the scheme names it once more before
        // the path.
        using var request = new HttpRequestMessage(
            HttpMethod.Head, "http://127.0.0.1:10000/fbptestaccount/family-backups/site-a/descriptor");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:y73YF680kias6msXfPKXje7PueRfDtwCK5ZfXd2S274=", AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_ADelete_MatchesTheReferenceSignature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, "https://fbptestaccount.blob.example.net/family-backups/site-a/repo/blobs/meta/cd/key");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:nh/2IELYBlecOyWLiY2bUnTX3XQd4bsggMcPRE4WGB4=", AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_AListingPage_SortsAndDecodesItsQuery_AsTheCanonicalResourceRequires()
    {
        // Out of order, with an opaque marker and every character a query
        // must encode: the signature covers the decoded, sorted form.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fbptestaccount.blob.example.net/family-backups?restype=container&comp=list"
            + "&prefix=site-a%2Frepo%2Fblobs%2F&marker=2%2196%21MDAwMDQ2IWJsb2Jz&maxresults=1000&delimiter=%2F");

        new AzureBlobRequestSigner(Account, Key).Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:UUKAhdGlhz5X5UQ5+aCFRw960f/kZH+14khlNNlyLuc=", AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_Again_ReplacesTheEarlierSignature_AtItsOwnTime()
    {
        // A retried request is signed again: a signature older than the
        // store's window would be refused, and two would be refused outright.
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, "https://fbptestaccount.blob.example.net/family-backups/site-a/repo/blobs/meta/cd/key");
        var signer = new AzureBlobRequestSigner(Account, Key);

        signer.Sign(request, At.AddMinutes(-20));
        signer.Sign(request, At);

        Assert.AreEqual("SharedKey fbptestaccount:nh/2IELYBlecOyWLiY2bUnTX3XQd4bsggMcPRE4WGB4=", AuthorizationOf(request));
        Assert.AreEqual("Tue, 06 Oct 2026 12:00:00 GMT", Assert.ContainsSingle(request.Headers.GetValues("x-ms-date")));
    }

    [TestMethod]
    public void Sign_UnderASharedAccessSignature_CarriesTheTokenAsIssued_AndNoAuthorization()
    {
        // The token is the signature: the store recomputes it from its own
        // fields, so one character of it re-encoded is a request refused.
        const string token = "sv=2024-11-04&sr=c&sp=racwdl&se=2026-12-31T00%3A00%3A00Z&spr=https&sig=AbC%2Bd%2Fe%3D";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fbptestaccount.blob.example.net/family-backups?restype=container&comp=list&prefix=site-a%2F");

        new AzureBlobRequestSigner(Account, AzureBlobCredentials.SharedAccessSignature("?" + token)).Sign(request, At);

        Assert.IsFalse(request.Headers.Contains("Authorization"));
        Assert.AreEqual(
            "https://fbptestaccount.blob.example.net/family-backups?restype=container&comp=list&prefix=site-a%2F&" + token,
            request.RequestUri!.OriginalString);
        Assert.AreEqual(AzureBlobRequestSigner.ApiVersion, Assert.ContainsSingle(request.Headers.GetValues("x-ms-version")));
    }

    [TestMethod]
    public void Sign_AgainUnderASharedAccessSignature_CarriesTheTokenOnce()
    {
        const string token = "sv=2024-11-04&sr=c&sp=r&sig=AbC%2Bd%2Fe%3D";
        using var request = new HttpRequestMessage(
            HttpMethod.Head, "https://fbptestaccount.blob.example.net/family-backups/site-a/descriptor");
        var signer = new AzureBlobRequestSigner(Account, AzureBlobCredentials.SharedAccessSignature(token));

        signer.Sign(request, At);
        signer.Sign(request, At);

        Assert.AreEqual(
            "https://fbptestaccount.blob.example.net/family-backups/site-a/descriptor?" + token,
            request.RequestUri!.OriginalString);
    }

    [TestMethod]
    public void Credentials_ToString_WithholdsTheSecret_OfEitherKind()
    {
        var key = Key.ToString();
        var sas = AzureBlobCredentials.SharedAccessSignature("sv=2024-11-04&sr=c&sp=r&sig=SECRETSIGNATURE").ToString();

        Assert.Contains("SharedKey", key, StringComparison.Ordinal);
        Assert.DoesNotContain("BwgJCgsM", key, StringComparison.Ordinal);
        Assert.Contains("SharedAccessSignature", sas, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETSIGNATURE", sas, StringComparison.Ordinal);
    }

    private static string AuthorizationOf(HttpRequestMessage request) =>
        Assert.ContainsSingle(request.Headers.GetValues("Authorization"));
}
