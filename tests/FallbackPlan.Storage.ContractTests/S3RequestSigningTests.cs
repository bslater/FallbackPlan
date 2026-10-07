using System.Net.Http.Headers;
using FallbackPlan.Storage.S3;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The S3-compatible provider signs every request with SigV4 as the S3 API
/// specifies it (FR-REP-002, ADR-0091). Each case is one shape of request the
/// provider sends, and each answer was computed for exactly that request by
/// an independent SigV4 implementation — one checked first against the S3
/// API reference's own worked examples, all four of which it reproduces — so
/// a signer that agrees with it agrees with the specification, and one that
/// drifts fails here rather than at a store that refuses every request.
/// </summary>
[TestClass]
public sealed class S3RequestSigningTests
{
    private static readonly S3Credentials Credentials =
        new("FBPTESTACCESSKEY0001", "fbp/test+secret=key/0123456789abcdefghij");

    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Sign_ARangedReadOfAVirtualHostedBucket_MatchesTheIndependentSignature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://backups.objects.example.net/repo/blobs/data/ab/key");
        request.Headers.Range = new RangeHeaderValue(0, 1023);

        new S3RequestSigner(Credentials, "eu-test-1").Sign(request, S3RequestSigner.EmptyPayloadSha256, At);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=FBPTESTACCESSKEY0001/20261006/eu-test-1/s3/aws4_request, "
            + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, "
            + "Signature=753cfb96b97ad26e8fd5ac7053bcb8d1d96277a716dcd40197c4060171f29c76",
            AuthorizationOf(request));
        Assert.AreEqual("20261006T120000Z", Assert.ContainsSingle(request.Headers.GetValues("x-amz-date")));
    }

    [TestMethod]
    public void Sign_ACreateOnlyPutOfAPathStyleBucket_SignsTheConditionAndThePayload()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, "https://objects.example.net/backups/site-a/repo/journal/w/0001");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        new S3RequestSigner(Credentials, "eu-test-1").Sign(
            request, "6c75b21b37c62d405bc2a0a6e43308723e8ddd4838923c8bfbd94c81646ff3a2", At);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=FBPTESTACCESSKEY0001/20261006/eu-test-1/s3/aws4_request, "
            + "SignedHeaders=host;if-none-match;x-amz-content-sha256;x-amz-date, "
            + "Signature=0dfe8bd455cbdb822684d6d99e38d5d340941848021ffd85f8376b8a4ff78570",
            AuthorizationOf(request));
        Assert.AreEqual(
            "6c75b21b37c62d405bc2a0a6e43308723e8ddd4838923c8bfbd94c81646ff3a2",
            Assert.ContainsSingle(request.Headers.GetValues("x-amz-content-sha256")));
    }

    [TestMethod]
    public void Sign_AHeadOnAPortOfThisMachine_SignsTheHostWithItsPort()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "http://127.0.0.1:9000/backups/repo/descriptor");

        new S3RequestSigner(Credentials, "us-east-1").Sign(request, S3RequestSigner.EmptyPayloadSha256, At);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=FBPTESTACCESSKEY0001/20261006/us-east-1/s3/aws4_request, "
            + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, "
            + "Signature=ad478e6dc4a170b33f96fac7b630a742fe85158b178425dc7b0f803a74cf3205",
            AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_ADelete_MatchesTheIndependentSignature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, "https://objects.example.net/backups/repo/blobs/meta/cd/key");

        new S3RequestSigner(Credentials, "eu-test-1").Sign(request, S3RequestSigner.EmptyPayloadSha256, At);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=FBPTESTACCESSKEY0001/20261006/eu-test-1/s3/aws4_request, "
            + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, "
            + "Signature=04b0377aa1b2dde1ebb48c8af385ea0cf49c812edbd4f5932718b5b2cafae96a",
            AuthorizationOf(request));
    }

    [TestMethod]
    public void Sign_AListingPage_SortsAndEncodesItsQuery_AsTheCanonicalRequestRequires()
    {
        // Out of order and with every character a query must encode: the
        // signature covers the canonical form, not the order it was written in.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://objects.example.net/backups?list-type=2&prefix=repo%2Fblobs%2F&start-after=repo%2Fblobs%2Fab"
            + "&max-keys=1000&continuation-token=1%2Fab%2B%3D");

        new S3RequestSigner(Credentials, "eu-test-1").Sign(request, S3RequestSigner.EmptyPayloadSha256, At);

        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=FBPTESTACCESSKEY0001/20261006/eu-test-1/s3/aws4_request, "
            + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, "
            + "Signature=2440630eb436cfe5694b9a385cd13b04b2fa8bf20f38c65d53156c0bcd9f080c",
            AuthorizationOf(request));
    }

    [TestMethod]
    public void Credentials_ToString_WithholdsTheSecret()
    {
        var said = Credentials.ToString();

        Assert.Contains("FBPTESTACCESSKEY0001", said, StringComparison.Ordinal);
        Assert.DoesNotContain("fbp/test+secret", said, StringComparison.Ordinal);
    }

    private static string AuthorizationOf(HttpRequestMessage request) =>
        Assert.ContainsSingle(request.Headers.GetValues("Authorization"));
}
