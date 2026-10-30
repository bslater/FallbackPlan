using System.Net;
using System.Net.Sockets;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The port a test dials to meet a store nobody answers. A connection to it
/// is refused, and no other process on the machine can be listening on it:
/// unlike a port a test bound and released, it is never one the operating
/// system hands a listener that asks for any free port, as the test runner's
/// own loopback channel does. Such a release once let a test's retried
/// request reach the runner's coverage channel, which read the request as a
/// malformed message and failed a run whose every test had passed.
/// </summary>
[TestClass]
public sealed class RefusedPortTests
{
    [TestMethod]
    public async Task AConnectionToIt_IsRefused()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var refused = await Assert.ThrowsAsync<SocketException>(async () =>
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, LoopbackPorts.Refused), timeout.Token));

        Assert.AreEqual(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "only Linux states the range it hands out in a file every account may read")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void ItIsBelowThePortsThisMachineHandsOut()
    {
        var range = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range")
            .Split((char[])['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Assert.IsLessThan(int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture), LoopbackPorts.Refused);
    }
}
