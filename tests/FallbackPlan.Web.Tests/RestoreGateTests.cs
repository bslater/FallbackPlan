using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's passphrase gate (FR-WOR-007, ADR-0089; FR-WOR-002): the
/// typed passphrase is derived here, in the console process, under the facts
/// the service publishes for each set — its salt, its cost and its sealing
/// key — and proved against that set's key before a grant is sealed to the
/// service's recipient key. Nothing local is read, so a console on any
/// machine verifies alike and there is nothing to continue without; the
/// passphrase never leaves this process; a wrong one mints nothing; and a
/// run of wrong ones is slowed, never locked.
/// </summary>
[TestClass]
public sealed class RestoreGateTests
{
    private const string Right = "the right passphrase!!";
    private const string Adopted = "the adopted set's own passphrase";

    /// <summary>Cheap derivation facts: stored costs are facts, accepted below the creation minimums.</summary>
    private static readonly Argon2Parameters Cheap = new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private static HttpRequestMessage Gate(ConsoleHarness harness, string json, string? tokenOverride = null, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/restore-gate", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", tokenOverride ?? harness.Auth.Token);
        if (session is not null)
        {
            request.Headers.Add(WebConsoleHost.SessionHeader, session);
        }

        return request;
    }

    /// <summary>The salt and sealing key a passphrase gives under <see cref="Cheap"/>, as the service publishes them.</summary>
    private static (string Salt, string SealingPublicKey) Facts(string passphraseText)
    {
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var passphrase = Passphrase.Create(passphraseText);
        using var authority = WriteOnlyDerivation.Derive(passphrase, Cheap, salt, KdfValidationMode.OpenRepository);
        return (Convert.ToHexStringLower(salt), Convert.ToHexStringLower(authority.Credential.SealingPublicKey));
    }

    /// <summary>
    /// A set-up installation with two sets: <c>docs</c> on the installation's
    /// facts, and <c>adopted</c>, brought in from a destination, on the salt
    /// it was born under and another passphrase.
    /// </summary>
    private static (ServiceDescriptionResult Description, BackupSetsResult Sets, byte[] RecipientScalar) Installation()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var installation = Facts(Right);
        var adopted = Facts(Adopted);
        var description = new ServiceDescriptionResult(
            "1.56", "test", "vm", "/state", false, 0,
            RestoreGrantRecipient: Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar)),
            KdfSalt: installation.Salt, KdfMemoryKib: Cheap.MemoryKiB, KdfIterations: Cheap.Iterations,
            KdfParallelism: Cheap.Parallelism, SealingPublicKey: installation.SealingPublicKey);
        var sets = new BackupSetsResult(
        [
            new BackupSetDescriptor(new string('a', 32), "docs", "/src", null, [], [], ["vault"]),
            new BackupSetDescriptor(
                new string('b', 32), "adopted", "/old", null, [], [], ["vault"],
                KdfSalt: adopted.Salt, KdfMemoryKib: Cheap.MemoryKiB, KdfIterations: Cheap.Iterations,
                KdfParallelism: Cheap.Parallelism, SealingPublicKey: adopted.SealingPublicKey),
        ]);
        return (description, sets, recipientScalar);
    }

    private static Func<ServiceCommand, ServiceResult> Answering(ServiceDescriptionResult description, BackupSetsResult sets) =>
        command => command switch
        {
            DescribeServiceCommand => description,
            ListBackupSetsCommand => sets,
            _ => new AcknowledgedResult(),
        };

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
    public async Task Gate_GrantsEachSetThePassphraseOpens_UnderThatSetsOwnFacts()
    {
        var (description, sets, recipientScalar) = Installation();
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Answering(description, sets);

        foreach (var (typed, opens, closed) in new[]
        {
            (Right, sets.Sets[0], sets.Sets[1]),
            (Adopted, sets.Sets[1], sets.Sets[0]),
        })
        {
            using var response = await harness.Http.SendAsync(Gate(harness, JsonSerializer.Serialize(new { passphrase = typed })));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("verified", body.RootElement.GetProperty("outcome").GetString());

            var grants = body.RootElement.GetProperty("grants");
            Assert.IsFalse(grants.TryGetProperty(closed.Id, out _), $"'{typed}' does not open {closed.Name}");

            // The grant is real: it opens with the service's recipient scalar
            // and reproduces the sealing key the service published for the set.
            var granted = WriteOnlyProvisioning.OpenGrant(
                recipientScalar, Convert.FromHexString(grants.GetProperty(opens.Id).GetString()!));
            var published = opens.SealingPublicKey ?? description.SealingPublicKey!;
            Assert.AreEqual(published, Convert.ToHexStringLower(ContentSealing.PublicKeyOf(granted)), opens.Name);
            CryptographicOperations.ZeroMemory(granted);
        }

        // Nothing passphrase-shaped reached the service: the gate asks only
        // what to derive under and what to seal to.
        Assert.IsTrue(
            harness.Clients.Client.Received.All(command => command is DescribeServiceCommand or ListBackupSetsCommand),
            string.Join(", ", harness.Clients.Client.Received.Select(command => command.GetType().Name)));
    }

    [TestMethod]
    public async Task Gate_AWrongPassphrase_MintsNothing()
    {
        var (description, sets, _) = Installation();
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Answering(description, sets);

        using var response = await harness.Http.SendAsync(Gate(harness, """{"passphrase":"not the passphrase"}"""));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual("wrong", body.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(
            body.RootElement.TryGetProperty("grants", out var grants) && grants.ValueKind == JsonValueKind.Object
                && grants.EnumerateObject().Any(),
            "a wrong passphrase mints nothing");
    }

    [TestMethod]
    public async Task Gate_WhereNothingIsPublishedToDeriveUnder_SaysUnavailable()
    {
        // A service not yet set up publishes no recipient and no facts; the
        // gate says so rather than pretending to check, and there is no
        // proceeding without it.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = Answering(
            new ServiceDescriptionResult("1.56", "test", "vm", "/state", false, 0),
            new BackupSetsResult([new BackupSetDescriptor(new string('a', 32), "docs", "/src", null, [], [], ["vault"])]));

        using var response = await harness.Http.SendAsync(Gate(harness, """{"passphrase":"whatever"}"""));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual("unavailable", body.RootElement.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void BuildRestoreGrants_AnUnusableRecipientKey_IsNamedNotBlamedOnASet()
    {
        // A service publishing a non-hex or wrong-length recipient key is its
        // own finding, never a wrong passphrase.
        var (description, sets, _) = Installation();
        foreach (var unusable in new[] { "this is not hex", "abcd" })
        {
            var answer = ConsoleRestoreGate.BuildRestoreGrants(
                description with { RestoreGrantRecipient = unusable }, sets.Sets, Right);
            Assert.AreEqual(ConsoleRestoreGate.GateOutcome.Unavailable, answer.Outcome, unusable);
            Assert.Contains("grant-recipient", answer.Detail!, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void TheThrottle_SlowsARunOfWrongOnes_AndNeverLocks()
    {
        // Three slips are free; after that each further try waits twice as
        // long as the last, up to half a minute, and never longer — the
        // passphrase's owner can always get in (the FR-USR-005 posture).
        TimeSpan[] expected =
        [
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        ];
        for (var wrong = 0; wrong < expected.Length; wrong++)
        {
            Assert.AreEqual(expected[wrong], RestoreGateThrottle.DelayAfter(wrong), $"after {wrong} wrong");
        }

        Assert.AreEqual(TimeSpan.FromSeconds(30), RestoreGateThrottle.DelayAfter(int.MaxValue));
    }

    [TestMethod]
    public async Task Gate_ARunOfWrongPassphrases_IsSlowedPerAccount_AndTheRightOneForgivesIt()
    {
        var (description, sets, _) = Installation();
        var waits = new List<TimeSpan>();
        var throttle = new RestoreGateThrottle((delay, _) =>
        {
            lock (waits)
            {
                waits.Add(delay);
            }

            return Task.CompletedTask;
        });
        await using var harness = await ConsoleHarness.StartAsync(
            configure: options => options with { RestoreGateThrottle = throttle });
        harness.Clients.Client.Respond = SignedIn(
            description, sets, new Dictionary<string, string>
            {
                ["amy-first-sign-in"] = "amy",
                ["amy-second-sign-in"] = "amy",
                ["ben-sign-in"] = "ben",
            });

        async Task<string?> TryAsync(string passphrase, string session)
        {
            using var response = await harness.Http.SendAsync(
                Gate(harness, JsonSerializer.Serialize(new { passphrase }), session: session));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("outcome").GetString();
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.AreEqual("wrong", await TryAsync("a guess", "amy-first-sign-in"));
        }

        // Signing in again does not start the count over: it is the account's.
        Assert.AreEqual("wrong", await TryAsync("another guess", "amy-second-sign-in"));

        // Another person's account has its own count.
        Assert.AreEqual("verified", await TryAsync(Right, "ben-sign-in"));

        // The right passphrase still opens after the run — later, never not.
        Assert.AreEqual("verified", await TryAsync(Right, "amy-first-sign-in"));
        Assert.AreEqual("wrong", await TryAsync("a slip", "amy-second-sign-in"));

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) },
            waits,
            "the fourth to sixth guesses waited, the right one after them waited its turn, and nothing after it");
    }

    [TestMethod]
    public async Task Gate_TriesFromOneAccount_TakeTurns_SoABurstCannotShareAFreeCount()
    {
        var (description, sets, _) = Installation();
        var waits = new List<TimeSpan>();
        var firstWaitHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throttle = new RestoreGateThrottle(async (delay, cancellationToken) =>
        {
            lock (waits)
            {
                waits.Add(delay);
            }

            await firstWaitHeld.Task.WaitAsync(cancellationToken);
        });
        await using var harness = await ConsoleHarness.StartAsync(
            configure: options => options with { RestoreGateThrottle = throttle });
        var answer = SignedIn(description, sets, new Dictionary<string, string> { ["amy-sign-in"] = "amy" });
        var listed = 0;
        var burstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Clients.Client.Respond = command =>
        {
            if (command is ListBackupSetsCommand && Interlocked.Increment(ref listed) == 6)
            {
                burstArrived.TrySetResult();
            }

            return answer(command);
        };

        async Task<string?> TryAsync(string passphrase)
        {
            using var response = await harness.Http.SendAsync(
                Gate(harness, JsonSerializer.Serialize(new { passphrase }), session: "amy-sign-in"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("outcome").GetString();
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.AreEqual("wrong", await TryAsync("a slip"));
        }

        // Three guesses at once, after the free three. Were each to read the
        // count before any was counted, all three would wait one second;
        // taking turns, each waits as long as the guesses before it earn.
        var burst = new[] { TryAsync("guess one"), TryAsync("guess two"), TryAsync("guess three") };
        await burstArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        firstWaitHeld.SetResult();
        foreach (var outcome in await Task.WhenAll(burst))
        {
            Assert.AreEqual("wrong", outcome);
        }

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            waits,
            "each guess in the burst waited its turn behind the one before it");
    }

    /// <summary>
    /// Answers as <see cref="Answering"/> does, and says who is signed in by
    /// the session the console last resumed, as the service's describe does.
    /// </summary>
    private static Func<ServiceCommand, ServiceResult> SignedIn(
        ServiceDescriptionResult description, BackupSetsResult sets, IReadOnlyDictionary<string, string> accounts)
    {
        string? resumed = null;
        return command =>
        {
            switch (command)
            {
                case ResumeSessionCommand resume:
                    Volatile.Write(ref resumed, resume.Token);
                    return new AcknowledgedResult();
                case DescribeServiceCommand:
                    return description with
                    {
                        SignedInUser = Volatile.Read(ref resumed) is { } token ? accounts.GetValueOrDefault(token) : null,
                    };
                default:
                    return Answering(description, sets)(command);
            }
        };
    }
}
