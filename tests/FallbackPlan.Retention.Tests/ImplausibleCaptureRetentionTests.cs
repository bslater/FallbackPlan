using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// That an implausible capture time reaches retention and is kept there
/// (FR-GC-012, ADR-0078). The survey carries each snapshot's writer and its
/// place in that writer's order. A pass keeps a snapshot a wrong clock
/// misdated, whether it only reports or applies, and its report says why.
/// </summary>
/// <remarks>
/// <see cref="ImplausibleCaptureTimeTests"/> proves the rule over facts handed
/// to it. This proves it over an archive, where the facts come from the
/// standalone records' cleartext.
/// </remarks>
[TestClass]
public sealed class ImplausibleCaptureRetentionTests : IDisposable
{
    private const string PassphraseText = "implausible-capture-tests-passph!";

    private static readonly string SetId = new('b', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ClockReset = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] MisdatedId = Enumerable.Repeat((byte)0x5a, 16).ToArray();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-implausible-capture-tests", Guid.NewGuid().ToString("n"));

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    private string SpoolDirectory => Directory.CreateDirectory(Path.Combine(_root, "spool")).FullName;

    public ImplausibleCaptureRetentionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "implausible capture fodder");

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { KeepDaily = 7, MinGenerations = 1 },
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    [TestMethod]
    public async Task TheSurvey_CarriesEachSnapshotsWriter()
    {
        await BackUpAsync(Day1);
        var other = WriterId.FromBytes(Enumerable.Repeat((byte)0x77, 16).ToArray());
        var real = await RealSnapshotAsync();
        await PublishAsync(real.AddDays(2), other, intentSequence: 1);

        var facts = await SurveyAsync();

        var local = Convert.ToHexStringLower(LocalState.LoadOrCreate(StateDirectory).WriterId);
        Assert.AreEqual(local, facts.Single(fact => !IsMisdated(fact)).WriterId);
        Assert.AreEqual(new string('7', 32), facts.Single(IsMisdated).WriterId);

        // Another writer's counters say nothing about this writer's order.
        // Read as one order, a capture published first and dated two days
        // after the real one would be out of step with it.
        Assert.IsEmpty(RetentionPlanner.FindImplausible(facts, real.AddHours(1), TimeSpan.FromDays(1)));
    }

    [TestMethod]
    public async Task ASnapshotPublishedAfterARealOneButDatedBeforeIt_IsKept_AndTheReportSaysWhy()
    {
        await BackUpAsync(Day1);
        var real = await RealSnapshotAsync();
        await PublishAsync(ClockReset, LocalWriter, intentSequence: 9001);

        var dryRun = await RunAsync(apply: false, real.AddHours(1));

        var misdated = Assert.ContainsSingle(dryRun.Implausible);
        Assert.AreEqual(Convert.ToHexStringLower(MisdatedId), misdated.Snapshot.SnapshotId);
        Assert.AreEqual(ImplausibleCaptureTime.Behind, misdated.Direction);
        Assert.Contains(
            line => line.StartsWith(
                $"  keep {Convert.ToHexStringLower(MisdatedId)[..12]}… — implausible capture time (dated before earlier publications)",
                StringComparison.Ordinal),
            dryRun.Lines,
            string.Join(" | ", dryRun.Lines));
        Assert.Contains(
            line => line.StartsWith("would delete: 0 snapshot", StringComparison.Ordinal),
            dryRun.Lines,
            string.Join(" | ", dryRun.Lines));

        // Dated twenty-five years back, it was beyond every window and the
        // floor's one place went to the real capture, so an applied pass
        // condemned it.
        var applied = await RunAsync(apply: true, real.AddHours(1));
        Assert.AreEqual(0, applied.TombstonesWritten, string.Join(" | ", applied.Lines));
    }

    private WriterId LocalWriter => WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId);

    private static bool IsMisdated(SnapshotFact fact) =>
        string.Equals(fact.SnapshotId, Convert.ToHexStringLower(MisdatedId), StringComparison.Ordinal);

    private async Task<RetentionReport> RunAsync(bool apply, DateTimeOffset now)
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);

        // No destinations, so nothing waits on a sync and the gate holds
        // nothing: what the pass keeps, it keeps by policy or by flag.
        return await RetentionRunner.RunAsync(
            store, opened.Repository, new RetentionConfiguration { KeepDaily = 7, MinGenerations = 1 },
            [], name => sync.Find(SetId, name), _ => TrimVerification.None,
            LocalWriter, apply, (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None,
            reclaim: apply ? opened.Reclaim : null);
    }

    private async Task<List<SnapshotFact>> SurveyAsync()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        return [.. (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots
            .Select(snapshot => snapshot.Fact)];
    }

    /// <summary>When the real backup completed, by its own manifest.</summary>
    private async Task<DateTimeOffset> RealSnapshotAsync() =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            (long)Assert.ContainsSingle(await SurveyAsync()).CapturedAtUnixMilliseconds);

    /// <summary>
    /// Publishes a standalone snapshot object into the archive the real
    /// backup created, dated <paramref name="capturedAt"/> and sealed under
    /// <paramref name="writer"/>'s <paramref name="intentSequence"/>. Its root
    /// tree is the real snapshot's, so the closure walks and the archive stays
    /// consistent: only its time, its writer and its place in that writer's
    /// order differ, which is what a capture taken under a wrong clock looks
    /// like.
    /// </summary>
    private async Task PublishAsync(DateTimeOffset capturedAt, WriterId writer, ulong intentSequence)
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;

        var existing = (await StagingMark.SurveyAsync(store, repository, CancellationToken.None)).Snapshots[0];
        var generation = new KeyGeneration((uint)Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value));

        var snapshot = new SnapshotManifest
        {
            SnapshotId = MisdatedId,
            DeviceId = existing.Manifest.DeviceId,
            BackupSetId = existing.Manifest.BackupSetId,
            CaptureStartedAt = (ulong)capturedAt.ToUnixTimeMilliseconds(),
            CaptureCompletedAt = (ulong)capturedAt.ToUnixTimeMilliseconds(),
            RootTree = existing.Manifest.RootTree,
            PolicyManifest = existing.Manifest.PolicyManifest,
            ConsistencyMethod = existing.Manifest.ConsistencyMethod,
            CaptureStatus = 1,
            SourceFilesystem = existing.Manifest.SourceFilesystem,
            PublicationGeneration = generation.Value,
            ClientVersion = existing.Manifest.ClientVersion,
        };

        byte[] encoded;
        using (var signer = RepositorySigner.Create(repository.Credential, generation))
        {
            encoded = SnapshotManifestCodec.Encode(
                snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
        }

        var builder = new ManifestBuilder(
            repository.RepositoryId, writer, generation, repository.Keys, store,
            new MonotonicBlobCounterAllocator(9000), SpoolDirectory,
            BlobWriteProfile.LocalDefault,
            FormatVersions.SealedDataPlane);

        await using (builder.ConfigureAwait(false))
        {
            await builder.WriteStandaloneSnapshotAsync(snapshot, encoded, intentSequence, CancellationToken.None);
        }
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
