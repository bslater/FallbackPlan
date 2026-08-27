using System.Net;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The restore wizard's passphrase gate (ADR-0041): verified in the console
/// process against real key files — never sent to the service, whose only
/// contribution is naming WHERE the archives live via describe_service. The
/// endpoint is token-guarded like every other, and a console with nothing
/// local to check says so honestly rather than pretending to verify.
/// </summary>
[TestClass]
public sealed class RestoreGateTests
{
    private static HttpRequestMessage Gate(ConsoleHarness harness, string json, string? tokenOverride = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/restore-gate", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", tokenOverride ?? harness.Auth.Token);
        return request;
    }

    [TestMethod]
    public async Task Gate_WithoutTheToken_IsRefusedBeforeAnythingRuns()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = Gate(harness, """{"passphrase":"anything"}""", tokenOverride: "not-the-token");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received, "an unauthenticated gate call must not touch the service");
    }

    [TestMethod]
    public async Task Gate_WithoutAPassphrase_IsMalformed()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = Gate(harness, """{"passphrase":""}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Gate_AgainstARealArchive_VerifiesRightAndRefusesWrong()
    {
        var archives = Path.Combine(Path.GetTempPath(), "fbp-gate", Guid.NewGuid().ToString("n")[..12]);
        var archive = Path.Combine(archives, new string('a', 32));
        Directory.CreateDirectory(archive);
        try
        {
            using (var right = Passphrase.Create("the right passphrase!!"))
            {
                (await RepositoryLifecycle.CreateAsync(
                    new LocalFileSystemObjectStore(archive), right, RepositoryCreationSettings.Default,
                    1_722_700_000_000UL, CancellationToken.None)).Dispose();
            }

            await using var harness = await ConsoleHarness.StartAsync();
            harness.Clients.Client.Respond = _ => new ServiceDescriptionResult(
                "1.11", "test", "vm", "/state", false, 0, ArchivesRoot: archives);

            using var good = Gate(harness, """{"passphrase":"the right passphrase!!"}""");
            using var verified = await harness.Http.SendAsync(good);
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode);
            using (var body = JsonDocument.Parse(await verified.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("verified", body.RootElement.GetProperty("outcome").GetString());
            }

            using var bad = Gate(harness, """{"passphrase":"not the passphrase"}""");
            using var wrong = await harness.Http.SendAsync(bad);
            using (var body = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
            }

            // Nothing passphrase-shaped ever reached the (fake) service: the
            // only command the gate sends is describe_service.
            Assert.IsTrue(
                harness.Clients.Client.Received.All(command => command is DescribeServiceCommand),
                "the gate may ask the service only where the archives live");
        }
        finally
        {
            try
            {
                Directory.Delete(archives, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task Gate_OnADirectShipOnlyInstall_VerifiesAgainstTheMetadataStore()
    {
        // A direct-ship set stages nothing: its key objects live in the
        // metadata store at <state>/sets/<set id> (ADR-0046), and an install
        // whose every set ships direct has an EMPTY archives root. The gate
        // must scan the metadata stores too, or such an install can never
        // verify a restore passphrase without a service restart onto the
        // staging path.
        var scratch = Path.Combine(Path.GetTempPath(), "fbp-gate-ship", Guid.NewGuid().ToString("n")[..12]);
        var archives = Path.Combine(scratch, "archives");
        var state = Path.Combine(scratch, "state");
        var metadata = Path.Combine(state, "sets", new string('a', 32));
        Directory.CreateDirectory(archives);
        Directory.CreateDirectory(metadata);
        try
        {
            using (var right = Passphrase.Create("the right passphrase!!"))
            {
                (await RepositoryLifecycle.CreateAsync(
                    new LocalFileSystemObjectStore(metadata), right, RepositoryCreationSettings.Default,
                    1_722_700_000_000UL, CancellationToken.None)).Dispose();
            }

            await using var harness = await ConsoleHarness.StartAsync();
            harness.Clients.Client.Respond = _ => new ServiceDescriptionResult(
                "1.19", "test", "vm", state, false, 0, ArchivesRoot: archives);

            using var good = Gate(harness, """{"passphrase":"the right passphrase!!"}""");
            using var verified = await harness.Http.SendAsync(good);
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode);
            using (var body = JsonDocument.Parse(await verified.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("verified", body.RootElement.GetProperty("outcome").GetString());
            }

            using var bad = Gate(harness, """{"passphrase":"not the passphrase"}""");
            using var wrong = await harness.Http.SendAsync(bad);
            using (var body = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task Gate_WithNothingLocalToCheck_SaysUnavailableRatherThanPretending()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ServiceDescriptionResult(
            "1.11", "test", "vm", "/state", false, 0,
            ArchivesRoot: Path.Combine(Path.GetTempPath(), "fbp-gate-none", Guid.NewGuid().ToString("n")));

        using var request = Gate(harness, """{"passphrase":"whatever"}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("unavailable", body.RootElement.GetProperty("outcome").GetString());
    }
}
