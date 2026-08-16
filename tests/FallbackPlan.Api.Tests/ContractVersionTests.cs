using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// Version negotiation (FR-SVC-007). The failure this prevents has a name: an
/// unexplained blank window, which is how users of a legacy backup service met
/// a version skew.
/// </summary>
[TestClass]
public sealed class ContractVersionTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api-ver", Guid.NewGuid().ToString("n")[..12]);

    public ContractVersionTests() => Directory.CreateDirectory(_state);

    // A transport test that hangs is a transport test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    [DataRow("1.0", 1, 0)]
    [DataRow("2.17", 2, 17)]
    public void TryParse_WellFormedVersionText_RoundTripsThroughItsRendering(string text, int major, int minor)
    {
        Assert.IsTrue(ContractVersion.TryParse(text, out var parsed));
        Assert.AreEqual(new ContractVersion(major, minor), parsed);
        Assert.AreEqual(text, parsed.ToString());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1")]
    [DataRow(".1")]
    [DataRow("one.zero")]
    public void TryParse_TextIsNotAVersion_RefusesRatherThanGuesses(string? text)
    {
        Assert.IsFalse(ContractVersion.TryParse(text, out _));
    }

    [TestMethod]
    public void IsCompatibleWith_WhenOnlyTheMinorDiffers_ShouldReturnTrue()
    {
        Assert.IsTrue(new ContractVersion(1, 0).IsCompatibleWith(new ContractVersion(1, 9)));
        Assert.IsFalse(new ContractVersion(1, 0).IsCompatibleWith(new ContractVersion(2, 0)));
    }

    [TestMethod]
    public void DescribeMismatch_WhenMajorsDiffer_ShouldNameBothVersions()
    {
        var message = ContractVersion.DescribeMismatch(new ContractVersion(1, 0), new ContractVersion(3, 4));

        Assert.Contains("1.0", message, StringComparison.Ordinal);
        Assert.Contains("3.4", message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ContractNegotiation_ClientSpeaksIncompatibleMajor_RefusesByNameRatherThanDisconnecting()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);

        // Speak the wire protocol by hand, because the shipped client cannot
        // be made to claim a version it does not have — which is the point.
        await using var stream = await OpenClientStreamAsync(Timeout);
        await FrameCodec.WriteAsync(
            stream, new HelloFrame("99.0", "impostor"), Timeout);

        var reply = await FrameCodec.ReadAsync(stream, Timeout);

        Assert.IsInstanceOfType<HelloAcknowledgementFrame>(reply, out var acknowledgement);
        Assert.IsFalse(acknowledgement.Accepted);
        Assert.Contains("99.0", acknowledgement.Message!, StringComparison.Ordinal);
        Assert.Contains(ContractVersion.Current.ToString(), acknowledgement.Message!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ContractNegotiation_ClientConnects_ReportsTheVersionTheServiceSpeaks()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        Assert.AreEqual(ContractVersion.Current, client.ServiceContractVersion);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        _timeout.Dispose();
    }

    private async Task<Stream> OpenClientStreamAsync(CancellationToken cancellationToken)
    {
        var address = LocalEndpoint.AddressFor(_state);

        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                address,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(address), cancellationToken);
        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
    }
}
