using System.Net;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The decoding NFR-PRIV-001's positive control reads the capture through,
/// held to payloads the runtime actually wrote. DefaultBuildSilenceTests shows
/// the instrument is not blind by finding the configured peer's endpoint among
/// the connects it decoded, so a misreading there is a control that cannot
/// find the connection it just watched being made.
///
/// The case that matters is the dual-mode socket. On a host with an IPv6
/// stack the runtime's default socket is IPv6 with IPv4 mapping enabled, and
/// dialling 127.0.0.1 through it writes the destination as ::ffff:127.0.0.1 —
/// family InterNetworkV6, 28 bytes. It is the same destination, reached over
/// IPv4. A host with no IPv6 stack writes plain InterNetwork instead, which is
/// why the control passed on one machine and failed on GitHub's runners.
///
/// Supports NFR-PRIV-001 by pinning the instrument, not the requirement's
/// claim, which DefaultBuildSilenceTests and TelemetrySilenceTests carry.
/// </summary>
[TestClass]
public sealed class NetworkEventTests
{
    // Verbatim from the positive control failing on an ubuntu runner: port
    // 45501, flow label 0, ::ffff:127.0.0.1, scope 0.
    private const string DualModeLoopbackConnect =
        "InterNetworkV6:28:{177,189,0,0,0,0,0,0,0,0,0,0,0,0,0,0,255,255,127,0,0,1,0,0,0,0}";

    [TestMethod]
    public void ADualModeConnect_ToAnIPv4Destination_DecodesAsThatIPv4Endpoint()
    {
        var connect = ConnectTo(DualModeLoopbackConnect);

        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 45501), connect.InternetEndpoint);
    }

    [TestMethod]
    public void AGenuineIPv6Connect_StaysAnIPv6Endpoint()
    {
        // ::1 carries no IPv4 address, and MapToIPv4 would still turn it into
        // 0.0.0.1 without complaint: only a mapped address may be unwrapped.
        var connect = ConnectTo(
            "InterNetworkV6:28:{177,189,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0}");

        Assert.AreEqual(new IPEndPoint(IPAddress.IPv6Loopback, 45501), connect.InternetEndpoint);
    }

    [TestMethod]
    public void ADualModeConnect_IsStillCountedAsAConnectToAnIpNetwork()
    {
        // Unwrapping the destination must not reclassify the event. The
        // negative assertions count connects by the family the runtime wrote,
        // and a mapped address leaves the machine as surely as any other.
        var connect = ConnectTo(DualModeLoopbackConnect);

        Assert.AreEqual("InterNetworkV6", connect.Family);
        Assert.IsTrue(connect.IsInternet);
    }

    private static NetworkEvent ConnectTo(string address) =>
        new("System.Net.Sockets", "ConnectStart", [new KeyValuePair<string, string>("address", address)]);
}
