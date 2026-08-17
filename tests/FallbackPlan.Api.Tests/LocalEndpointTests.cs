using System.Runtime.Versioning;
using System.Text;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// Where the local socket lives (ADR-0028 §5), and what happens when the
/// obvious place does not fit. POSIX caps a socket path at ~104 bytes, and a
/// real state directory crosses that with nothing more exotic than macOS's
/// per-user temp root or a long username under <c>~/Library</c> — so "choose
/// a shorter directory" is not an answer a backup product gets to give.
/// </summary>
/// <remarks>
/// The fallback tested here must be <em>deterministic</em>: the service and
/// every client derive the address independently from the state directory
/// alone, so there is no pointer file to go stale — the failure mode the
/// prior art's token files made famous.
/// </remarks>
[TestClass]
[DoNotParallelize] // The per-user fallback directory is machine-global state.
public sealed class LocalEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fbp-endpoint-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public LocalEndpointTests() => Directory.CreateDirectory(_root);

    /// <summary>A state directory long enough that its socket path cannot bind.</summary>
    private string DeepStateDirectory(char filler)
    {
        var path = Path.Combine(_root, new string(filler, 110), "state");
        Directory.CreateDirectory(path);
        Assert.IsGreaterThan(
            LocalEndpoint.MaximumSocketPathBytes,
            Encoding.UTF8.GetByteCount(Path.Combine(path, "service.sock")),
            "the fixture path fits after all, so nothing below tests the fallback");
        return path;
    }

    [PlatformCondition(TestPlatforms.Posix, "the limit is sockaddr_un's; Windows pipes have no path")]
    [TestMethod]
    public void AddressFor_AStateDirectoryWithinTheLimit_KeepsTheSocketBesideTheState()
    {
        var state = Path.Combine(_root, "s");
        Directory.CreateDirectory(state);

        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(state), "service.sock"),
            LocalEndpoint.AddressFor(state));
    }

    [PlatformCondition(TestPlatforms.Posix, "the limit is sockaddr_un's; Windows pipes have no path")]
    [TestMethod]
    public void AddressFor_AStateDirectoryBeyondTheLimit_FallsBackToAShortDeterministicPath()
    {
        var state = DeepStateDirectory('a');

        var first = LocalEndpoint.AddressFor(state);
        var second = LocalEndpoint.AddressFor(state);

        Assert.IsLessThanOrEqualTo(
            LocalEndpoint.MaximumSocketPathBytes,
            Encoding.UTF8.GetByteCount(first),
            "the fallback path does not fit the platform limit either");
        Assert.AreEqual(first, second, "the address is not deterministic — a client could not find the service");
        Assert.IsFalse(
            first.StartsWith(Path.GetFullPath(state), StringComparison.Ordinal),
            "the fallback still lives under the too-long state directory");

        // Distinct state directories stay on distinct sockets, which is what
        // keeps several repositories on one machine independent.
        Assert.AreNotEqual(first, LocalEndpoint.AddressFor(DeepStateDirectory('b')));
    }

    [PlatformCondition(TestPlatforms.Posix, "permission bits are the authentication (ADR-0028 §5)")]
    [TestMethod]
    [UnsupportedOSPlatform("windows")]
    public void AddressFor_TheFallbackDirectory_AdmitsItsOwnerOnly()
    {
        var socket = LocalEndpoint.AddressFor(DeepStateDirectory('c'));
        var directory = Path.GetDirectoryName(socket)!;

        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory),
            "the fallback directory is reachable by other users — permissions are the authentication");
    }

    /// <summary>
    /// A fallback directory another user could write into is a socket
    /// hijack waiting for a victim, so it is refused, never repaired: fixing
    /// the permissions of a directory somebody else prepared would be
    /// adopting their trap.
    /// </summary>
    [PlatformCondition(TestPlatforms.Posix, "permission bits are the authentication (ADR-0028 §5)")]
    [TestMethod]
    [UnsupportedOSPlatform("windows")]
    public void AddressFor_AFallbackDirectoryOthersCanWrite_IsRefusedNotAdopted()
    {
        var state = DeepStateDirectory('d');
        var directory = Path.GetDirectoryName(LocalEndpoint.AddressFor(state))!;

        try
        {
            File.SetUnixFileMode(
                directory,
                File.GetUnixFileMode(directory) | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var refusal = Assert.ThrowsExactly<IOException>(() => LocalEndpoint.AddressFor(state));
            Assert.Contains("permissions", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [PlatformCondition(TestPlatforms.Posix, "the limit is sockaddr_un's; Windows pipes have no path")]
    [TestMethod]
    public async Task LocalBinding_OverTheFallbackAddress_CarriesACommandRoundTrip()
    {
        var state = DeepStateDirectory('e');
        var service = new FakeService { Respond = _ => new JobAcceptedResult("job-deep") };

        await using var listener = LocalServiceListener.Start(service, state);
        await using var client = await LocalServiceClient.ConnectAsync(state, "test", _timeout.Token);

        var result = await client.ExecuteAsync(new RunBackupCommand("documents", Full: false), _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(result, out var accepted);
        Assert.AreEqual("job-deep", accepted.JobId);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
