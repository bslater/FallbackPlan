using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.TestSupport;
using FallbackPlan.Web;
using Microsoft.Playwright;

namespace FallbackPlan.Web.DomTests;

// The fake client and the console harness here are deliberate copies of
// their Web.Tests counterparts. This repository has no InternalsVisibleTo
// anywhere and that absence is a recorded position; the duplication is the
// price of that rule and is meant to be visible — the same trade
// ConsoleLogging documents between the Web and Recovery hosts. If the copies
// drift, they drift because the suites' needs drifted, which is information.

/// <summary>
/// A service client that does nothing but answer, so the browser suite tests
/// the page for what it is: markup, gating, and behaviour against a console
/// whose service always answers as told.
/// </summary>
internal sealed class FakeServiceClient : IFallbackPlanClient
{
    private readonly Channel<JobProgressEvent> _progress = Channel.CreateUnbounded<JobProgressEvent>();

    private long _sequence;

    public List<ServiceCommand> Received { get; } = [];

    public Func<ServiceCommand, ServiceResult> Respond { get; set; } = _ => new AcknowledgedResult();

    public ContractVersion ServiceContractVersion => ContractVersion.Current;

    public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        lock (Received)
        {
            Received.Add(command);
        }

        return ValueTask.FromResult(Respond(command));
    }

    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var progress in _progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    public void Emit(FallbackPlan.Domain.Jobs.JobProgress progress) =>
        _progress.Writer.TryWrite(new JobProgressEvent(Interlocked.Increment(ref _sequence), progress));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The factory the console is started against.</summary>
internal sealed class FakeClientFactory : IServiceClientFactory
{
    public FakeServiceClient Client { get; } = new();

    /// <summary>When set, every connect fails the way an absent service does.</summary>
    public bool Unreachable { get; set; }

    public string Address => "(fake state directory)";

    public ValueTask<IFallbackPlanClient> ConnectAsync(CancellationToken cancellationToken) =>
        Unreachable
            ? throw new ServiceConnectionException("No service is listening at '(fake state directory)'.")
            : ValueTask.FromResult<IFallbackPlanClient>(Client);
}

/// <summary>
/// One running console on an ephemeral loopback port against the fake client,
/// plus the tokened URL a browser opens it with.
/// </summary>
internal sealed class DomHarness : IAsyncDisposable
{
    private readonly string _state;

    private DomHarness(string state, RunningConsole console, ConsoleAuth auth, FakeClientFactory clients)
    {
        _state = state;
        Console = console;
        Auth = auth;
        Clients = clients;
    }

    public RunningConsole Console { get; }

    public ConsoleAuth Auth { get; }

    public FakeClientFactory Clients { get; }

    /// <summary>The page URL carrying this run's token, as the terminal would print it.</summary>
    public string TokenedUrl => $"{Console.BaseAddress}?token={Auth.Token}";

    public static async Task<DomHarness> StartAsync()
    {
        var state = Path.Combine(Path.GetTempPath(), "fbp-dom", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(state);

        var auth = ConsoleAuth.CreateWithRandomToken();
        var clients = new FakeClientFactory();
        var console = await WebConsoleHost.StartAsync(
            new WebConsoleOptions { StateDirectory = state, Port = 0 }, clients, auth);

        return new DomHarness(state, console, auth, clients);
    }

    /// <summary>
    /// Waits for a command matching <paramref name="matches"/> to arrive at
    /// the fake — the page's actions round-trip through real HTTP, so a
    /// click's command lands a beat after the click returns.
    /// </summary>
    public async Task<TCommand> ReceivedAsync<TCommand>(
        Func<TCommand, bool>? matches = null, int timeoutMilliseconds = 30_000)
        where TCommand : ServiceCommand
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            lock (Clients.Client.Received)
            {
                var found = Clients.Client.Received.OfType<TCommand>()
                    .FirstOrDefault(command => matches is null || matches(command));
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException(
            $"No {typeof(TCommand).Name} arrived at the fake within {timeoutMilliseconds} ms.");
    }

    public async ValueTask DisposeAsync()
    {
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

/// <summary>
/// The wire shapes the suites keep reaching for — one place to mint a
/// describe answer and the common descriptors, so a test's fake reads as
/// its scenario rather than as constructor plumbing.
/// </summary>
internal static class Wire
{
    /// <summary>The set identity the suites use throughout.</summary>
    public static readonly string SetId = new('a', 32);

    /// <summary>A device identity for kits and describes.</summary>
    public const string DeviceIdHex = "00112233445566778899aabbccddeeff";

    public static ServiceDescriptionResult Describe(
        string setupState, string? signedInUser = null, string? archivesRoot = "/archives") =>
        new(
            "1.18", "test", "vm", "/state", false, 0,
            ArchivesRoot: archivesRoot,
            RestoreGrantRecipient: System.Convert.ToHexStringLower(
                FallbackPlan.Repository.Crypto.ContentSealing.PublicKeyOf(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            SetupState: setupState,
            DeviceId: DeviceIdHex,
            SignedInUser: signedInUser);

    public static BackupSetDescriptor Set(string name = "docs") =>
        new(SetId, name, "/src", null, [], [], ["vault"]);

    public static SnapshotDescriptor Snapshot(ulong capturedAt, string id = "snap-1") =>
        new(id, SetId, capturedAt, CaptureStatus: 1, Files: 3, ConsistencyMethod: 1);
}

/// <summary>
/// One headless Chromium for the whole assembly. Launched only where the run
/// opted in (<see cref="BrowserConditionAttribute.Variable"/>) — assembly
/// initialisation runs even when every test is condition-skipped, and a
/// launch attempt on a machine with no browser would turn those skips into
/// errors.
/// </summary>
[TestClass]
public sealed class BrowserSession
{
    private static IPlaywright? _playwright;

    /// <summary>The shared browser; null when the run did not opt in.</summary>
    public static IBrowser? Browser { get; private set; }

    /// <summary>A new isolated context accepting downloads.</summary>
    public static Task<IBrowserContext> NewContextAsync() =>
        Browser!.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext context)
    {
        _ = context;
        if (Environment.GetEnvironmentVariable(BrowserConditionAttribute.Variable) != "1")
        {
            return;
        }

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // The ceremony's finish runs a real Argon2 derivation in the console;
        // the library's five-second default assertion window is tuned for
        // pages that only shuffle DOM, not for one that does key derivation.
        Assertions.SetDefaultExpectTimeout(30_000);
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
