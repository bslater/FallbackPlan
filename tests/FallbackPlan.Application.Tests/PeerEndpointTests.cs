using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// Taking <c>host:port</c> apart (FR-DEST-006, FR-DEST-009).
/// </summary>
/// <remarks>
/// <para>
/// This class exists because of a defect found by reading fifteen years of
/// Duplicati's release notes, where "the URL parser returned different results
/// for similar inputs", "a leading slash could be stripped from the path or a
/// URL", "scheme detection on short paths" and "better detection of valid S3
/// hostnames" are separate shipped fixes across separate releases. Address
/// parsing is not a solved problem you inherit for free; it is a place every
/// backup tool bleeds.
/// </para>
/// <para>
/// The specific defect here: splitting on the last colon accepted
/// <c>fe80::1</c> as host <c>fe80:</c> on port <c>1</c>. An address a person
/// plainly meant as portless was called well formed by status and then dialled
/// at a host that cannot exist — which is precisely what checking an address
/// before the first sync is supposed to prevent.
/// </para>
/// </remarks>
[TestClass]
public sealed class PeerEndpointTests
{
    [TestMethod]
    [DataRow("friend.example:9443", "friend.example", 9443)]
    [DataRow("192.0.2.10:7040", "192.0.2.10", 7040)]
    [DataRow("[::1]:9443", "::1", 9443)]
    [DataRow("[fe80::1%eth0]:1", "fe80::1%eth0", 1)]
    [DataRow("h:65535", "h", 65535)]
    public void TryParse_AWellFormedEndpoint_YieldsItsHostAndPort(string endpoint, string host, int port)
    {
        Assert.IsTrue(PeerEndpoint.TryParse(endpoint, out var parsedHost, out var parsedPort, out var defect), defect);
        Assert.AreEqual(host, parsedHost, "the brackets belong to the notation, not to the host");
        Assert.AreEqual(port, parsedPort);
        Assert.IsNull(defect);
    }

    [TestMethod]
    [DataRow("fe80::1")]
    [DataRow("::1")]
    [DataRow("2001:db8::8a2e:370:7334")]
    public void TryParse_AnIPv6AddressWithNoPort_IsRefusedRatherThanSplitAtTheLastColon(string endpoint)
    {
        // The defect this class was written for. Splitting on the last colon
        // hands back a host ending in a colon and a port made of the final
        // group — well formed by every check that mattered, dialable by none.
        Assert.IsFalse(PeerEndpoint.TryParse(endpoint, out _, out _, out var defect));
        Assert.IsNotNull(defect);
        Assert.Contains("IPv6", defect, StringComparison.Ordinal);
        Assert.Contains($"[{endpoint}]:", defect, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("friend.example", "not host:port")]
    [DataRow(":9443", "not host:port")]
    [DataRow("host:", "not host:port")]
    [DataRow("host:abc", "not host:port")]
    [DataRow("[::1:9443", "never closes it")]
    [DataRow("[]:9443", "not host:port")]
    [DataRow("[::1]9443", "not host:port")]
    [DataRow("host:0", "not a port")]
    [DataRow("host:70000", "not a port")]
    [DataRow("host:-1", "not host:port")]
    public void TryParse_AMalformedEndpoint_IsRefusedAndSaysWhy(string endpoint, string because)
    {
        Assert.IsFalse(PeerEndpoint.TryParse(endpoint, out _, out _, out var defect), $"'{endpoint}' was accepted");
        Assert.Contains(because, defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TryParse_APortWrittenWithCultureFlourishes_IsRefusedRatherThanReinterpreted()
    {
        // Parsed invariantly and with no NumberStyles allowed, so a group
        // separator, a sign or surrounding whitespace cannot be read one way
        // by the check and another by the dialler. Duplicati shipped
        // "locale-sensitive parsing bug for fr-CA" and "invariant formatting
        // causing crashes during backup" as separate fixes; a port is digits
        // in every language.
        Assert.IsFalse(PeerEndpoint.TryParse("host:9 443", out _, out _, out _));
        Assert.IsFalse(PeerEndpoint.TryParse("host:9,443", out _, out _, out _));
        Assert.IsFalse(PeerEndpoint.TryParse("host: 9443", out _, out _, out _));
        Assert.IsFalse(PeerEndpoint.TryParse("host:+9443", out _, out _, out _));
    }
}
