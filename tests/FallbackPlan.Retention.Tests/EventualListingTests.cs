using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A collection pass against a store whose listings lag
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md);
/// architecture 05 §1). Establishes FR-GC-002 and NFR-PORT-005.
/// </summary>
/// <remarks>
/// <para>
/// Collection is absence-based from end to end: a blob is garbage because
/// nothing reachable names it, and what is reachable is what a listing of
/// <c>snapshots/</c> could enumerate. Against a store that cannot promise a
/// listing reflects what it holds, absence is not a fact — and a snapshot the
/// listing has not caught up to is not merely missing, it is
/// <b>indistinguishable from one that was never written</b>. It decodes
/// perfectly, so it adds nothing to the survey's undecodable list and vetoes
/// nothing; its blobs, listed from a plane that <em>is</em> current, read as
/// fully dead.
/// </para>
/// <para>
/// The two cases below are the same repository under the same policy, and the
/// only difference between them is what a listing could see.
/// </para>
/// </remarks>
[TestClass]
public sealed class EventualListingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-eventual-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "eventual-listing-passphrase!";
    private static readonly string SetId = new('c', 32);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");

    public EventualListingTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task ASnapshotTheListingHasNotCaughtUpTo_CondemnsNothing_AndSaysWhyItCondemnsNothing()
    {
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var inner = await ThreeDaysAsync(day1);

        // Every snapshot is protected, so a pass over an honest listing has
        // nothing to do: this is the baseline the lagging pass is measured
        // against, and it is what makes the lag the only variable.
        var honest = await RunAsync(inner, apply: false, day1.AddDays(2).AddHours(1));
        Assert.Contains("would delete: 0 snapshot object(s), 0 blob(s)", honest.Lines);
        Assert.IsEmpty(honest.Lines.Where(NoDeletion));

        var newest = (await ListAsync(inner, "snapshots/"))
            .OrderBy(key => key, StringComparer.Ordinal).Last();

        var lagging = new LaggingObjectStore(inner);
        lagging.Conceal(key => string.Equals(key, newest, StringComparison.Ordinal));
        Assert.HasCount(2, await ListAsync(lagging, "snapshots/"));

        var lagged = await RunAsync(lagging, apply: true, day1.AddDays(2).AddHours(1));

        // The plan is still computed and still reported — it is what a
        // strongly-consistent store would have collected, and saying so is
        // more use than silence. What must not happen is acting on it.
        Assert.Contains(
            line => line.StartsWith("would delete:", StringComparison.Ordinal)
                && !line.EndsWith("0 blob(s)", StringComparison.Ordinal),
            lagged.Lines);

        var veto = Assert.ContainsSingle(lagged.Lines.Where(NoDeletion));
        Assert.Contains("listing consistency: eventual", veto, StringComparison.Ordinal);
        Assert.Contains("absence", veto, StringComparison.Ordinal);

        // Nothing was condemned, so nothing waits out a grace and nothing is
        // one publication away from going.
        Assert.AreEqual(0, lagged.TombstonesWritten);
        Assert.IsEmpty(await ListAsync(inner, "tombstones/"));
    }

    [TestMethod]
    public async Task OnceTheListingCatchesUp_TheSamePassCollectsExactlyAsItWouldHave()
    {
        // The veto is a refusal to act on a lag, not a refusal to collect: a
        // store that says Eventual and is nonetheless current is still
        // refused, because the promise is what the planner can reason about
        // and the state of the day is not. What ends the refusal is the
        // promise, which for this instrument is what Release models.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var inner = await ThreeDaysAsync(day1);

        var lagging = new LaggingObjectStore(inner);
        lagging.Conceal(_ => false);

        var stillRefused = await RunAsync(lagging, apply: true, day1.AddDays(2).AddHours(1));
        Assert.IsNotEmpty(stillRefused.Lines.Where(NoDeletion));
        Assert.AreEqual(0, stillRefused.TombstonesWritten);

        // The same archive through a store that promises Strong collects.
        var honest = await RunAsync(inner, apply: true, day1.AddDays(2).AddHours(1));
        Assert.IsEmpty(honest.Lines.Where(NoDeletion));
    }

    [TestMethod]
    public async Task ADestinationsKeepSet_IsRefusedRatherThanBuiltFromAListingThatMayLag()
    {
        // Convergence is the same absence-based reasoning executed somewhere
        // worse: the keep-set is built from the source's listing and applied
        // as deletions at a destination that may hold the only other copy.
        var day1 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var inner = await ThreeDaysAsync(day1);

        using var opened = await WriteOnlyInstallation.OpenAsync(inner, PassphraseText, CancellationToken.None);

        var honest = await DestinationConvergence.ComputeKeepsAsync(
            inner, opened.Repository,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 3 },
            day1.AddDays(2).AddHours(1),
            CancellationToken.None);
        Assert.IsNull(honest.Refusal);
        Assert.IsNotNull(honest.Keeps);

        var lagging = new LaggingObjectStore(inner);
        var refused = await DestinationConvergence.ComputeKeepsAsync(
            lagging, opened.Repository,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 3 },
            day1.AddDays(2).AddHours(1),
            CancellationToken.None);

        Assert.AreEqual(ConvergenceRefusal.LaggingListing, refused.Refusal);

        // A refusal is not a trim of nothing: it is no filter at all, which
        // is what makes the destination keep everything it holds.
        Assert.IsNull(refused.Keeps);
    }

    private static bool NoDeletion(string line) =>
        line.StartsWith("NO DELETION", StringComparison.Ordinal);

    private async Task<LocalFileSystemObjectStore> ThreeDaysAsync(DateTimeOffset day1)
    {
        await BackUpAsync(day1);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(day1.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(day1.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        Assert.HasCount(3, await ListAsync(store, "snapshots/"));
        return store;
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private async Task<RetentionReport> RunAsync(IObjectStore store, bool apply, DateTimeOffset now)
    {
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store, opened.Repository,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 3 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name),
            _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            apply,
            (ulong)now.ToUnixTimeMilliseconds(),
            CancellationToken.None,
            "docs",
            reclaim: opened.Reclaim);
    }

    private static async Task<List<string>> ListAsync(IObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
