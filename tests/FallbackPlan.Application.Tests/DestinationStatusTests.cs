using FallbackPlan.Application;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The one place a destination's declaration, its ledger row and the set's last
/// backup are combined into what the status derivation consumes. It exists so
/// the service handler and the console cannot answer the same question
/// differently — they had already drifted into deriving the failure domain and
/// the in-sync-but-behind demotion twice each.
/// </summary>
[TestClass]
public sealed class DestinationStatusTests
{
    private const string SetRoot = "/home/someone/documents";

    private static DestinationConfiguration LocalPath(string path = "/mnt/vault") => new()
    {
        Id = new string('1', 32),
        Name = "vault",
        Kind = DestinationKind.LocalPath,
        Path = path,
    };

    private static DestinationSyncRecord Row(
        DestinationSyncState state, ulong? lastSuccessAt = null, ulong lastAttemptAt = 1_000) => new()
        {
            SetId = new string('a', 32),
            Destination = "vault",
            State = state,
            LastAttemptAt = lastAttemptAt,
            LastSuccessAt = lastSuccessAt,
        };

    /// <summary>
    /// A fixed clock, well past the ledger stamps the fixtures use, so the
    /// tests that are not about age are not accidentally about age.
    /// </summary>
    private const ulong Now = 100_000_000_000;

    /// <summary>Every path on its own volume — nothing shares a device.</summary>
    private static ulong? DistinctDevice(string path) => (ulong)path.Length;

    /// <summary>One volume for everything, the same-disk case.</summary>
    private static ulong? OneDevice(string path) => 7UL;

    /// <summary>A platform that will not answer.</summary>
    private static ulong? Unknown(string path) => null;

    [TestMethod]
    public void Describe_InSyncButOlderThanTheLastBackup_IsBehind()
    {
        // The staging archive moved on. Both producers computed this
        // separately before; a set that reads "in sync" while the newest
        // snapshot has not crossed is the lie the demotion exists to stop.
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, Row(DestinationSyncState.InSync, lastSuccessAt: 1_000),
            lastCompletedAt: 5_000, Now, DistinctDevice);

        Assert.AreEqual(DestinationSyncState.Behind, input.Sync);
    }

    [TestMethod]
    public void Describe_InSyncAndCurrent_StaysInSync()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, Row(DestinationSyncState.InSync, lastSuccessAt: 5_000),
            lastCompletedAt: 5_000, Now, DistinctDevice);

        Assert.AreEqual(DestinationSyncState.InSync, input.Sync);
    }

    [TestMethod]
    public void Describe_NeverAttempted_IsBehindRatherThanInvented()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, record: null, lastCompletedAt: 5_000, Now, DistinctDevice);

        Assert.AreEqual(DestinationSyncState.Behind, input.Sync);
        Assert.IsNull(input.LastSuccessAt);
    }

    [TestMethod]
    public void Describe_ADanglingReference_ReportsItAndEarnsNothing()
    {
        // Validation refuses these, so it means the file was edited under a
        // running service. The conservative domain matters: an undeclarable
        // destination must not count toward protection.
        var input = DestinationStatus.Describe(
            "ghost", declared: null, SetRoot, record: null, lastCompletedAt: 0, Now, DistinctDevice);

        Assert.AreEqual("ghost", input.Name);
        Assert.AreEqual(DestinationSyncState.Failed, input.Sync);
        Assert.AreEqual(FailureDomain.SameVolume, input.Domain);
        Assert.AreEqual("no longer declared", input.Detail);
    }

    [TestMethod]
    public void DomainOf_ALocalPathOnAnotherVolume_IsSameMachineAndNoFurther()
    {
        // A second disk survives losing the first and nothing else — never
        // same-site, never independent (FR-SNP-007).
        Assert.AreEqual(
            FailureDomain.SameMachine,
            DestinationStatus.DomainOf(LocalPath(), SetRoot, DistinctDevice));
    }

    [TestMethod]
    public void DomainOf_ALocalPathSharingTheSourceVolume_IsSameVolume()
    {
        Assert.AreEqual(
            FailureDomain.SameVolume,
            DestinationStatus.DomainOf(LocalPath(), SetRoot, OneDevice));
    }

    [TestMethod]
    public void DomainOf_APlatformThatWillNotSay_AnswersConservatively()
    {
        // Unknown must never be optimistic: guessing "different disk" would
        // let a same-disk copy read as protection.
        Assert.AreEqual(
            FailureDomain.SameVolume,
            DestinationStatus.DomainOf(LocalPath(), SetRoot, Unknown));
    }

    [TestMethod]
    public void DomainOf_ADeclaredDomain_WinsOverTheDerivedOne()
    {
        var declared = LocalPath() with { FailureDomain = FailureDomain.Independent };

        Assert.AreEqual(
            FailureDomain.Independent,
            DestinationStatus.DomainOf(declared, SetRoot, OneDevice));
    }

    [TestMethod]
    public void DomainOf_APeerWithNoDeclaration_IsSameSiteNotIndependent()
    {
        // A friend on the same LAN does not survive the house fire
        // (ADR-0018 Amendment 2).
        var peer = new DestinationConfiguration
        {
            Id = new string('2', 32),
            Name = "friend",
            Kind = DestinationKind.Peer,
            Fingerprint = "mgr7e7euwdpfkggmp4astkz5ia",
            Endpoint = "alice.example.com:7040",
        };

        Assert.AreEqual(FailureDomain.SameSite, DestinationStatus.DomainOf(peer, SetRoot, DistinctDevice));
    }

    [TestMethod]
    public void Describe_CarriesTheLedgersVerificationStampsThrough()
    {
        // The stamps are what `verified` is earned from; dropping one here
        // would silently downgrade a proven destination.
        var record = Row(DestinationSyncState.InSync, lastSuccessAt: 5_000) with
        {
            SyncedSequence = 42,
            VerifiedAt = 6_000,
            VerifiedSequence = 42,
            VerifiedObjects = 4,
            VerifiedPopulation = 12,
        };

        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, record, lastCompletedAt: 5_000, Now, DistinctDevice);

        Assert.AreEqual(42UL, input.SyncedSequence);
        Assert.AreEqual(6_000UL, input.VerifiedAt);
        Assert.AreEqual(4, input.VerifiedObjects);
        Assert.AreEqual(12, input.VerifiedPopulation);
        Assert.IsTrue(input.IsProvenCurrent);
    }

    [TestMethod]
    public void Describe_TheAgeOfTheProof_IsWhereTheClockEnters()
    {
        // Day 1 and day 400 read identically everywhere else: the derivation
        // is a pure function of plain values and takes no clock, and the
        // ledger's stamp is a bare instant. This is the only place the two
        // meet.
        var input = Proven(verifiedAt: Day(0), now: Day(9));

        Assert.AreEqual(9, input.VerificationAgeDays);
        Assert.AreEqual(DestinationStatus.LocalPathVerificationBoundDays, input.VerificationBoundDays);
        Assert.IsTrue(input.VerificationOverdue);
    }

    [TestMethod]
    public void Describe_EachKind_CarriesItsOwnBound()
    {
        // Architecture 09 §4's example policy: seven days for a local path,
        // thirty for a peer. Nine days is overdue for one and fine for the
        // other, which is the whole reason the bound travels per-row rather
        // than as one number in the derivation.
        var local = Proven(verifiedAt: Day(0), now: Day(9));
        var peer = Proven(verifiedAt: Day(0), now: Day(9), kind: DestinationKind.Peer);

        Assert.AreEqual(7, local.VerificationBoundDays);
        Assert.AreEqual(30, peer.VerificationBoundDays);
        Assert.IsTrue(local.VerificationOverdue);
        Assert.IsFalse(peer.VerificationOverdue);
    }

    [TestMethod]
    public void Describe_AProofExactlyOnItsBound_IsNotYetOverdue()
    {
        Assert.IsFalse(Proven(verifiedAt: Day(0), now: Day(7)).VerificationOverdue);
        Assert.IsTrue(Proven(verifiedAt: Day(0), now: Day(8)).VerificationOverdue);
    }

    [TestMethod]
    public void Describe_AStampAheadOfTheClock_WithholdsTheAgeRatherThanReadingAsFresh()
    {
        // A skewed clock, not a fresh proof. Reporting age zero would read as
        // "verified today" — the one answer that is certainly wrong. No age
        // means no overdue warning, which can only fail to warn.
        var input = Proven(verifiedAt: Day(9), now: Day(0));

        Assert.IsNull(input.VerificationAgeDays);
        Assert.IsFalse(input.VerificationOverdue);
    }

    [TestMethod]
    public void Describe_ADestinationExcusedFromProving_IsNeverOverdue()
    {
        // FR-VER-006: it was knowingly accepted unprovable and already earns
        // its own warning saying so on every pass. An age complaint on top
        // would be a second line about a decision somebody already made.
        var input = Proven(verifiedAt: Day(0), now: Day(400)) with { RequiresVerification = false };

        Assert.IsFalse(input.VerificationOverdue);
    }

    [TestMethod]
    public void Describe_AProofThatNoLongerCoversWhatWasSent_IsStaleRatherThanOverdue()
    {
        // The two must not both fire: sequence-staleness already says the
        // proof does not cover the latest sync, and that is the actionable
        // fact. Overdue is for the destination with no other symptom.
        var input = Proven(verifiedAt: Day(0), now: Day(400)) with { SyncedSequence = 99 };

        Assert.IsFalse(input.IsProvenCurrent);
        Assert.IsFalse(input.VerificationOverdue);
    }

    [TestMethod]
    public void Describe_ADestinationNeverProven_IsUnprovenRatherThanOverdue()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, Row(DestinationSyncState.InSync, lastSuccessAt: 1_000),
            lastCompletedAt: 1_000, Day(400), DistinctDevice);

        Assert.IsNull(input.VerificationAgeDays);
        Assert.IsFalse(input.VerificationOverdue);
    }

    /// <summary>Sixteen bytes in lowercase unpadded base32 — the shape a real fingerprint has.</summary>
    private const string WellFormedFingerprint = "mgr7e7euwdpfkggmp4astkz5ia";

    [TestMethod]
    public void Describe_AWellFormedAddress_ReportsNoDefect()
    {
        Assert.IsNull(
            DestinationStatus.Describe(
                "vault", LocalPath(), SetRoot, record: null, lastCompletedAt: 0, Now, DistinctDevice)
                .AddressDefect);

        Assert.IsNull(Peer().AddressDefect);
    }

    [TestMethod]
    public void Describe_ARelativeLocalPath_IsNamedRatherThanResolvedAgainstWhereverTheServiceStarted()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath("backups/vault"), SetRoot, record: null, lastCompletedAt: 0, Now, DistinctDevice);

        Assert.IsNotNull(input.AddressDefect);
        Assert.Contains("absolute", input.AddressDefect, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Describe_AMistypedEndpoint_IsNamedBeforeAnySyncCountsOnIt()
    {
        // Today this surfaces in the fan-out, at the first sync — which for a
        // destination no set has synced yet is never.
        Assert.Contains(
            "host:port", Peer(endpoint: "friend.example").AddressDefect ?? "", StringComparison.Ordinal);
        Assert.Contains(
            "not a port", Peer(endpoint: "friend.example:70000").AddressDefect ?? "", StringComparison.Ordinal);
        Assert.Contains(
            "not a port", Peer(endpoint: "friend.example:0").AddressDefect ?? "", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Describe_AFingerprintThatIsNotOne_SaysWhereToGetTheRealOne()
    {
        // A truncated or hex-looking fingerprint can never match a grant, so
        // every sync would report "not paired" — true, and the wrong thing to
        // go and do about it.
        var input = Peer(fingerprint: new string('b', 64));

        Assert.IsNotNull(input.AddressDefect);
        Assert.Contains("base32", input.AddressDefect, StringComparison.Ordinal);
        Assert.Contains("pairings", input.AddressDefect, StringComparison.Ordinal);
    }

    private static DestinationStatusInput Peer(
        string fingerprint = WellFormedFingerprint, string endpoint = "friend.example:9443") =>
        DestinationStatus.Describe(
            "friend",
            new DestinationConfiguration
            {
                Id = new string('2', 32), Name = "friend", Kind = DestinationKind.Peer,
                Fingerprint = fingerprint, Endpoint = endpoint,
            },
            SetRoot, record: null, lastCompletedAt: 0, Now, DistinctDevice);

    private static ulong Day(int index) => 1_754_000_000_000UL + ((ulong)index * 86_400_000UL);

    /// <summary>A destination whose proof covers everything it was sent.</summary>
    private static DestinationStatusInput Proven(
        ulong verifiedAt, ulong now, DestinationKind kind = DestinationKind.LocalPath)
    {
        var declared = kind == DestinationKind.Peer
            ? new DestinationConfiguration
            {
                Id = new string('2', 32), Name = "friend", Kind = DestinationKind.Peer,
                Fingerprint = WellFormedFingerprint, Endpoint = "friend.example:9443",
            }
            : LocalPath();

        var record = Row(DestinationSyncState.InSync, lastSuccessAt: verifiedAt) with
        {
            SyncedSequence = 42,
            VerifiedAt = verifiedAt,
            VerifiedSequence = 42,
            VerifiedObjects = 4,
            VerifiedPopulation = 12,
        };

        return DestinationStatus.Describe(
            declared.Name, declared, SetRoot, record, lastCompletedAt: verifiedAt, now, DistinctDevice);
    }
}
