using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// Version negotiation (FR-SVC-007). The failure this prevents has a name: an
/// unexplained blank window, which is how CrashPlan users met a version skew.
/// </summary>
public sealed class ContractVersionTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api-ver", Guid.NewGuid().ToString("n")[..12]);

    public ContractVersionTests() => Directory.CreateDirectory(_state);

    // A transport test that hangs is a transport test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.17", 2, 17)]
    public void A_version_round_trips_through_its_rendering(string text, int major, int minor)
    {
        Assert.True(ContractVersion.TryParse(text, out var parsed));
        Assert.Equal(new ContractVersion(major, minor), parsed);
        Assert.Equal(text, parsed.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData(".1")]
    [InlineData("one.zero")]
    public void Anything_that_is_not_a_version_is_refused_rather_than_guessed(string? text)
    {
        Assert.False(ContractVersion.TryParse(text, out _));
    }

    [Fact]
    public void Compatibility_is_by_major_so_additive_changes_do_not_break_a_peer()
    {
        Assert.True(new ContractVersion(1, 0).IsCompatibleWith(new ContractVersion(1, 9)));
        Assert.False(new ContractVersion(1, 0).IsCompatibleWith(new ContractVersion(2, 0)));
    }

    [Fact]
    public void A_mismatch_message_names_both_versions()
    {
        var message = ContractVersion.DescribeMismatch(new ContractVersion(1, 0), new ContractVersion(3, 4));

        Assert.Contains("1.0", message, StringComparison.Ordinal);
        Assert.Contains("3.4", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_speaking_an_incompatible_contract_is_refused_by_name_not_disconnected()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);

        // Speak the wire protocol by hand, because the shipped client cannot
        // be made to claim a version it does not have — which is the point.
        await using var stream = await OpenClientStreamAsync(Timeout);
        await FrameCodec.WriteAsync(
            stream, new HelloFrame("99.0", "impostor"), Timeout);

        var reply = await FrameCodec.ReadAsync(stream, Timeout);

        var acknowledgement = Assert.IsType<HelloAcknowledgementFrame>(reply);
        Assert.False(acknowledgement.Accepted);
        Assert.Contains("99.0", acknowledgement.Message!, StringComparison.Ordinal);
        Assert.Contains(ContractVersion.Current.ToString(), acknowledgement.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connected_client_learns_the_version_the_service_speaks()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        Assert.Equal(ContractVersion.Current, client.ServiceContractVersion);
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
