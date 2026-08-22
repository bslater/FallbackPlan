using System.Diagnostics;
using FallbackPlan.Agent;
using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The installation's account store (FR-USR-001, FR-USR-002, FR-USR-004,
/// FR-USR-005, NFR-SEC-012; ADR-0045 §3, §6, §7): where accounts live, the
/// owner rules, and the throttle that never locks.
/// </summary>
[TestClass]
public sealed class UserStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-users-tests", Guid.NewGuid().ToString("n"));

    /// <summary>Cheap enough to create dozens of accounts in a suite, still a real derivation.</summary>
    private static readonly Argon2Parameters Fast = new()
    {
        MemoryKiB = 64,
        Iterations = 1,
        Parallelism = 1,
    };

    public UserStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>
    /// The real schedule scaled down by a thousand. The shape under test is
    /// "it doubles and then it stops"; sleeping through four real seconds
    /// twelve times over would prove the same thing and cost the suite a
    /// minute. One case below pins the numbers a service actually uses.
    /// </summary>
    private static readonly UserStore.ThrottlePolicy Brisk = new(
        TimeSpan.FromMilliseconds(0.25), TimeSpan.FromMilliseconds(4));

    private UserStore Open() => UserStore.Open(_root, throttle: Brisk);

    [TestMethod]
    public void Open_AFreshStateDirectory_HasNoAccounts()
    {
        // The state FR-USR-001 turns into "setup is not finished".
        Assert.IsFalse(Open().HasAccounts);
        Assert.IsEmpty(Open().List());
    }

    [TestMethod]
    public void Create_TheFirstAccount_IsTheOwnerWhateverWasAskedFor()
    {
        // An installation whose first account was not the owner would have no
        // account that cannot be deleted, which is the guarantee FR-USR-004
        // rests on.
        var store = Open();

        var created = store.Create("ben", "a-good-password", UserRole.Operator, Fast);

        Assert.IsTrue(created.IsOk);
        Assert.AreEqual(UserRole.Owner, created.User!.Role);
    }

    [TestMethod]
    public void Create_ASecondAccount_TakesTheRoleAsked()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        var second = store.Create("sam", "another-password", UserRole.Operator, Fast);

        Assert.IsTrue(second.IsOk);
        Assert.AreEqual(UserRole.Operator, second.User!.Role);
        Assert.HasCount(2, store.List());
    }

    [TestMethod]
    public void Create_ANameAlreadyTaken_IsRefusedRegardlessOfCase()
    {
        // Case-insensitive, because "Ben" and "ben" reading as two people is a
        // way to be impersonated by typing.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        Assert.AreEqual(UserStoreOutcome.NameTaken, store.Create("BEN", "different-one", parameters: Fast).Outcome);
        Assert.HasCount(1, store.List());
    }

    [TestMethod]
    public void Create_ANameThatCouldForgeALogLine_IsRefused()
    {
        // An account name goes into a log line, a refusal message and a
        // receipt, and one that can carry a line break can forge a second line
        // in all three.
        var store = Open();

        string[] hostile =
        [
            string.Empty,
            "   ",
            "two words",
            "line" + '\n' + "break",
            "tab" + '\t' + "separated",
            "null" + '\0' + "byte",
            new string('n', UserStore.MaximumNameLength + 1),
        ];

        foreach (var name in hostile)
        {
            Assert.AreEqual(
                UserStoreOutcome.NameRefused,
                store.Create(name, "a-good-password", parameters: Fast).Outcome,
                $"a name of length {name.Length} was accepted");
        }

        Assert.IsFalse(store.HasAccounts);
    }

    [TestMethod]
    public void Create_APasswordBelowTheFloor_IsRefused()
    {
        var store = Open();

        Assert.AreEqual(UserStoreOutcome.PasswordRefused, store.Create("ben", "short", parameters: Fast).Outcome);
        Assert.AreEqual(UserStoreOutcome.PasswordRefused, store.Create("ben", string.Empty, parameters: Fast).Outcome);
        Assert.IsFalse(store.HasAccounts);
    }

    [TestMethod]
    public async Task Verify_TheRightPassword_Accepts()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        var verified = await store.VerifyAsync("ben", "a-good-password", CancellationToken.None);

        Assert.IsTrue(verified.IsOk);
        Assert.AreEqual("ben", verified.User!.Name);
    }

    [TestMethod]
    public async Task Verify_AnUnknownAccount_AnswersLikeAWrongPassword()
    {
        // Not "no such user": a login that answers that instantly is a name
        // oracle, and telling an attacker which names exist is half the work.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        var unknown = await store.VerifyAsync("nobody", "a-good-password", CancellationToken.None);

        Assert.AreEqual(UserStoreOutcome.WrongPassword, unknown.Outcome);
        Assert.IsNull(unknown.User);
    }

    [TestMethod]
    public async Task Verify_RepeatedFailures_TakeLongerAndNeverLock()
    {
        // FR-USR-005 in one test: the delay grows, it stops growing, and the
        // correct password still works immediately afterwards. Nothing here
        // can put an account beyond its owner's reach.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        Assert.AreEqual(TimeSpan.Zero, store.PendingDelayFor("ben"), "nothing has failed yet");

        var first = Stopwatch.GetTimestamp();
        await store.VerifyAsync("ben", "wrong-once", CancellationToken.None);
        var firstElapsed = Stopwatch.GetElapsedTime(first);

        var delayAfterOne = store.PendingDelayFor("ben");
        Assert.IsGreaterThan(TimeSpan.Zero, delayAfterOne);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await store.VerifyAsync("ben", "wrong-again", CancellationToken.None);
        }

        var delayAfterFive = store.PendingDelayFor("ben");
        Assert.IsGreaterThan(delayAfterOne, delayAfterFive, "the delay grows with consecutive failures");
        Assert.IsLessThanOrEqualTo(Brisk.Maximum, delayAfterFive, "and stops growing at the cap");

        // The one that matters: after a run of failures the owner still gets in.
        var recovered = await store.VerifyAsync("ben", "a-good-password", CancellationToken.None);
        Assert.IsTrue(recovered.IsOk, "no sequence of failures may deny an account its own backups");
        Assert.AreEqual(TimeSpan.Zero, store.PendingDelayFor("ben"), "a success clears the count");

        // The first failure paid no delay — the delay applies to the next
        // attempt, not to the one that earned it — which is why five wrong
        // passwords take visibly longer than one.
        Assert.IsLessThan(Brisk.Maximum + TimeSpan.FromSeconds(1), firstElapsed);
    }

    [TestMethod]
    public async Task Verify_TheDelayIsCapped_SoAnAccountIsNeverEffectivelyLocked()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await store.VerifyAsync("ben", "wrong", CancellationToken.None);
        }

        Assert.AreEqual(
            Brisk.Maximum, store.PendingDelayFor("ben"),
            "twelve failures reach the cap and stay there — a lockout in slow motion is still a lockout");
    }

    [TestMethod]
    public void Delete_TheOwner_IsRefused()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);
        store.Create("sam", "another-password", UserRole.Operator, Fast);

        Assert.AreEqual(UserStoreOutcome.OwnerIsPermanent, store.Delete("ben").Outcome);
        Assert.IsTrue(store.Delete("sam").IsOk);
        Assert.AreEqual("ben", store.List().Single().Name);
    }

    [TestMethod]
    public void Delete_AnUnknownAccount_SaysSo()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        Assert.AreEqual(UserStoreOutcome.NoSuchUser, store.Delete("nobody").Outcome);
    }

    [TestMethod]
    public void MayManageAccounts_OnlyTheOwner_Answers()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);
        store.Create("sam", "another-password", UserRole.Operator, Fast);

        Assert.IsTrue(store.MayManageAccounts("ben"));
        Assert.IsFalse(store.MayManageAccounts("sam"));
        Assert.IsFalse(store.MayManageAccounts("nobody"));
    }

    [TestMethod]
    public async Task ChangePassword_ProvesTheCurrentOneFirst()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        var refused = await store.ChangePasswordAsync("ben", "not-it-at-all", "a-newer-password", CancellationToken.None);
        Assert.IsFalse(refused.IsOk);

        var changed = await store.ChangePasswordAsync(
            "ben", "a-good-password", "a-newer-password", CancellationToken.None);
        Assert.IsTrue(changed.IsOk);

        Assert.IsTrue((await store.VerifyAsync("ben", "a-newer-password", CancellationToken.None)).IsOk);
        Assert.IsFalse((await store.VerifyAsync("ben", "a-good-password", CancellationToken.None)).IsOk);
    }

    [TestMethod]
    public async Task ChangePassword_ToOneBelowTheFloor_IsRefusedAndKeepsTheOld()
    {
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);

        var refused = await store.ChangePasswordAsync("ben", "a-good-password", "short", CancellationToken.None);

        Assert.AreEqual(UserStoreOutcome.PasswordRefused, refused.Outcome);
        Assert.IsTrue((await store.VerifyAsync("ben", "a-good-password", CancellationToken.None)).IsOk);
    }

    [TestMethod]
    public async Task Accounts_SurviveAReopen_AndTheThrottleDoesNot()
    {
        // Both halves are decisions. Accounts are durable; throttle state is
        // not, because it exists to slow live guessing and writing it down
        // would let an attacker's failures outlive the process and become a
        // way to slow the account's owner down tomorrow.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);
        await store.VerifyAsync("ben", "wrong", CancellationToken.None);
        Assert.IsGreaterThan(TimeSpan.Zero, store.PendingDelayFor("ben"));

        var reopened = Open();

        Assert.IsTrue(reopened.HasAccounts);
        Assert.AreEqual(UserRole.Owner, reopened.List().Single().Role);
        Assert.IsTrue((await reopened.VerifyAsync("ben", "a-good-password", CancellationToken.None)).IsOk);
        Assert.AreEqual(TimeSpan.Zero, reopened.PendingDelayFor("ben"));
    }

    [TestMethod]
    public void Store_OnDisk_CarriesNoPasswordAndIsOwnerOnly()
    {
        // NFR-SEC-012, asserted against the bytes rather than against intent.
        var store = Open();
        store.Create("ben", "Sup3rSecretPhrase", parameters: Fast);

        var path = Path.Combine(_root, "users.json");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("Sup3rSecret", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fbp-argon2id$", text, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path),
                "an account file another local user can read is a list of names to guess against");
        }
    }

    [TestMethod]
    public void Open_ADamagedAccountFile_IsNamedRatherThanReplaced()
    {
        // Starting fresh would present an installation that has accounts as one
        // that has none — and the next caller to arrive would become its owner.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);
        File.WriteAllText(Path.Combine(_root, "users.json"), "{ not json");

        var refusal = Assert.ThrowsExactly<InvalidDataException>(Open);

        Assert.Contains("users.json", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("owner", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Open_AnUnknownRole_ReadsAsTheNarrowestOneRatherThanTheOwner()
    {
        // A file written by a later version that knows roles this one does not
        // must not be read as granting more than it says.
        var store = Open();
        store.Create("ben", "a-good-password", parameters: Fast);
        store.Create("sam", "another-password", UserRole.Operator, Fast);

        var path = Path.Combine(_root, "users.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Operator\"", "\"Auditor\"", StringComparison.Ordinal));

        var reopened = Open();

        Assert.AreEqual(UserRole.Operator, reopened.Find("sam")!.Role);
        Assert.IsFalse(reopened.MayManageAccounts("sam"));
    }
    [TestMethod]
    public void ThrottlePolicy_TheDefault_IsTheOneTheDecisionRecordDescribes()
    {
        // Every case above runs the schedule scaled down, so this is what keeps
        // "a few seconds" from quietly becoming a few minutes: the shape is
        // proven fast, the numbers are pinned here.
        Assert.AreEqual(UserStore.FirstFailureDelay, UserStore.ThrottlePolicy.Default.First);
        Assert.AreEqual(UserStore.MaximumFailureDelay, UserStore.ThrottlePolicy.Default.Maximum);

        Assert.IsGreaterThan(
            TimeSpan.Zero, UserStore.FirstFailureDelay,
            "a first failure that costs nothing is not a throttle");
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromSeconds(10), UserStore.MaximumFailureDelay,
            "past this the cap stops reading as a delay and starts reading as a lockout (ADR-0045 §7)");
    }
}
