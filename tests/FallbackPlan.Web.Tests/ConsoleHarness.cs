using FallbackPlan.Web;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// One running console on an ephemeral loopback port, against the fake client,
/// with an <see cref="HttpClient"/> aimed at it — the shape every test needs.
/// </summary>
internal sealed class ConsoleHarness : IAsyncDisposable
{
    private readonly string _state;

    private ConsoleHarness(string state, RunningConsole console, ConsoleAuth auth, FakeClientFactory clients)
    {
        _state = state;
        Console = console;
        Auth = auth;
        Clients = clients;
        Http = new HttpClient { BaseAddress = console.BaseAddress };
    }

    public RunningConsole Console { get; }

    public ConsoleAuth Auth { get; }

    public FakeClientFactory Clients { get; }

    public HttpClient Http { get; }

    public static async Task<ConsoleHarness> StartAsync()
    {
        var state = Path.Combine(Path.GetTempPath(), "fbp-web", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(state);

        var auth = ConsoleAuth.CreateWithRandomToken();
        var clients = new FakeClientFactory();
        var console = await WebConsoleHost.StartAsync(
            new WebConsoleOptions { StateDirectory = state, Port = 0 }, clients, auth);

        return new ConsoleHarness(state, console, auth, clients);
    }

    /// <summary>A request carrying this run's token as a bearer credential.</summary>
    public HttpRequestMessage Command(string json, string? tokenOverride = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/command", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", tokenOverride ?? Auth.Token);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Console.DisposeAsync();
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle on a temp directory is not a test failure.
        }
    }
}
