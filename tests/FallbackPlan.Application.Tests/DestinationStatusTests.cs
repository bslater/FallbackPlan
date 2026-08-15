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
            lastCompletedAt: 5_000, DistinctDevice);

        Assert.AreEqual(DestinationSyncState.Behind, input.Sync);
    }

    [TestMethod]
    public void Describe_InSyncAndCurrent_StaysInSync()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, Row(DestinationSyncState.InSync, lastSuccessAt: 5_000),
            lastCompletedAt: 5_000, DistinctDevice);

        Assert.AreEqual(DestinationSyncState.InSync, input.Sync);
    }

    [TestMethod]
    public void Describe_NeverAttempted_IsBehindRatherThanInvented()
    {
        var input = DestinationStatus.Describe(
            "vault", LocalPath(), SetRoot, record: null, lastCompletedAt: 5_000, DistinctDevice);

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
            "ghost", declared: null, SetRoot, record: null, lastCompletedAt: 0, DistinctDevice);

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
            Fingerprint = "mfzq6ysbmfzq6ysbmfzq6ysbmf",
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
            "vault", LocalPath(), SetRoot, record, lastCompletedAt: 5_000, DistinctDevice);

        Assert.AreEqual(42UL, input.SyncedSequence);
        Assert.AreEqual(6_000UL, input.VerifiedAt);
        Assert.AreEqual(4, input.VerifiedObjects);
        Assert.AreEqual(12, input.VerifiedPopulation);
        Assert.IsTrue(input.IsProvenCurrent);
    }
}
