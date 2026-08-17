using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// That a partial capture reaches retention as a partial capture (ledger O6 —
/// a partial backup producing a defective one once retention rules apply).
/// </summary>
/// <remarks>
/// <para>
/// <c>RetentionPlannerTests</c> proves the rules over facts handed to it
/// directly. This class proves the other half, which is where the defect
/// actually lived: <see cref="SnapshotManifest.CaptureStatus"/> has recorded
/// "1 complete, 2 partial" since the format was written, and the CLI has
/// rendered it since the beginning — but <see cref="StagingMark.SurveyAsync"/>
/// dropped it on the floor, so every snapshot arrived at the planner looking
/// complete and the rules had nothing to act on.
/// </para>
/// <para>
/// A rule that is correct over facts it never receives is not a fix.
/// </para>
/// </remarks>
[TestClass]
public sealed class PartialCaptureRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-partial-retention-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "partial-retention-tests-passphr!";
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    private string SpoolDirectory => Directory.CreateDirectory(Path.Combine(_root, "spool")).FullName;

    public PartialCaptureRetentionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "partial retention fodder");

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
                    Root = SourceRoot,
                    Schedule = "every 4h",
                    Retention = new RetentionConfiguration { MinGenerations = 1 },
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    /// <summary>A real backup that captured everything surveys as complete.</summary>
    [TestMethod]
    public async Task ACleanBackup_SurveysAsComplete()
    {
        await BackUpAsync(Day1);

        var fact = Assert.ContainsSingle(await SurveyAsync()).Fact;

        Assert.AreEqual<byte>(1, fact.CaptureStatus);
        Assert.IsTrue(fact.IsComplete);
    }

    /// <summary>
    /// And a snapshot whose manifest says partial surveys as partial. The
    /// manifest is written directly with <c>capture_status</c> 2 — the same
    /// value <c>OperationGateway</c> writes when a publication carries an
    /// error manifest — because the chmod-based route to a real partial
    /// capture needs an unprivileged process.
    /// </summary>
    [TestMethod]
    public async Task ASnapshotMarkedPartial_SurveysAsPartial()
    {
        await BackUpAsync(Day1);
        await WritePartialSnapshotAsync();

        var facts = await SurveyAsync();
        Assert.HasCount(2, facts);

        var partial = facts.Single(snapshot => snapshot.Fact.CaptureStatus == 2).Fact;
        Assert.IsFalse(partial.IsComplete);
        Assert.IsTrue(facts.Single(snapshot => snapshot.Fact.CaptureStatus == 1).Fact.IsComplete);
    }

    /// <summary>
    /// The two halves joined, which is the defect end to end: with
    /// <c>min_generations = 1</c> and a partial capture as the newest
    /// snapshot, the older complete one must survive. Before the survey
    /// carried the status it did not — the partial filled the floor and the
    /// set's only whole backup expired.
    /// </summary>
    [TestMethod]
    public async Task WithAPartialAsTheNewest_TheOlderCompleteBackupIsStillProtected()
    {
        await BackUpAsync(Day1);
        var partialAt = await WritePartialSnapshotAsync();

        var facts = await SurveyAsync();
        var selection = RetentionPlanner.Select(
            [.. facts.Select(snapshot => snapshot.Fact)],
            new RetentionConfiguration { MinGenerations = 1 },
            partialAt.AddHours(1));

        Assert.IsEmpty(
            selection.Expire,
            "a complete backup expired while the set's only other snapshot was a partial one");
        Assert.HasCount(2, selection.Keep);
    }

    /// <summary>
    /// Through the whole runner, so the interlock is proven where it is used
    /// rather than only where it is defined: the pass tombstones nothing.
    /// </summary>
    [TestMethod]
    public async Task AnApplyPassWithAPartialNewest_CondemnsNoSnapshot()
    {
        await BackUpAsync(Day1);
        var partialAt = await WritePartialSnapshotAsync();

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var sync = DestinationSyncStore.Open(StateDirectory);
        var report = await RetentionRunner.RunAsync(
            store, repository, new RetentionConfiguration { MinGenerations = 1 },
            [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: true,
            (ulong)partialAt.AddHours(1).ToUnixTimeMilliseconds(), CancellationToken.None);

        Assert.Contains(
            line => line.StartsWith("would delete: 0 snapshot", StringComparison.Ordinal),
            report.Lines,
            string.Join(" | ", report.Lines));
        Assert.AreEqual(0, report.TombstonesWritten);
    }

    private async Task<IReadOnlyList<SurveyedSnapshot>> SurveyAsync()
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);
        return (await StagingMark.SurveyAsync(store, repository, CancellationToken.None)).Snapshots;
    }

    /// <summary>
    /// An aborted capture (<c>capture_status</c> 3) is no more complete than a
    /// partial one. This implementation never publishes 3, but the format
    /// assigns it and the codec admits it on read, so retention must read it
    /// defensively: the floor is filled by status 1 alone, not by
    /// "anything that is not 2".
    /// </summary>
    [TestMethod]
    public async Task WithAnAbortedCaptureAsTheNewest_TheOlderCompleteBackupIsStillProtected()
    {
        await BackUpAsync(Day1);
        var abortedAt = await WritePartialSnapshotAsync(captureStatus: 3);

        var facts = await SurveyAsync();
        Assert.IsFalse(
            facts.Single(snapshot => snapshot.Fact.CaptureStatus == 3).Fact.IsComplete,
            "an aborted capture surveyed as complete");

        var selection = RetentionPlanner.Select(
            [.. facts.Select(snapshot => snapshot.Fact)],
            new RetentionConfiguration { MinGenerations = 1 },
            abortedAt.AddHours(1));

        Assert.IsEmpty(
            selection.Expire,
            "a complete backup expired while the set's only other snapshot was an aborted one");
        Assert.HasCount(2, selection.Keep);
    }

    /// <summary>
    /// Publishes a standalone snapshot object carrying the given
    /// <c>capture_status</c> into the archive an earlier backup created, one
    /// hour after it. Its root tree is the one the real snapshot already
    /// published, so the closure walks and the archive stays consistent —
    /// only the status differs.
    /// </summary>
    /// <returns>When the partial capture completed, so a caller can pick a clock after it.</returns>
    /// <remarks>
    /// The time is derived from the existing snapshot rather than from this
    /// class's fixed dates: a backup stamps <c>capture_completed_at</c> from
    /// the wall clock, not from the instant the pass was handed, so a
    /// hard-coded date would silently land <em>before</em> the real snapshot
    /// and the test would be exercising the opposite ordering to the one it
    /// claims.
    /// </remarks>
    private async Task<DateTimeOffset> WritePartialSnapshotAsync(byte captureStatus = 2)
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None);

        var existing = (await StagingMark.SurveyAsync(store, repository, CancellationToken.None)).Snapshots[0];
        var capturedAt = DateTimeOffset
            .FromUnixTimeMilliseconds((long)existing.Fact.CapturedAtUnixMilliseconds)
            .AddHours(1);
        var generation = new KeyGeneration((uint)Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value));

        var snapshot = new SnapshotManifest
        {
            SnapshotId = Enumerable.Repeat((byte)0x5a, 16).ToArray(),
            DeviceId = existing.Manifest.DeviceId,
            BackupSetId = existing.Manifest.BackupSetId,
            CaptureStartedAt = (ulong)capturedAt.ToUnixTimeMilliseconds(),
            CaptureCompletedAt = (ulong)capturedAt.ToUnixTimeMilliseconds(),
            RootTree = existing.Manifest.RootTree,
            PolicyManifest = existing.Manifest.PolicyManifest,
            ConsistencyMethod = existing.Manifest.ConsistencyMethod,

            // The one thing under test.
            CaptureStatus = captureStatus,
            SourceFilesystem = existing.Manifest.SourceFilesystem,
            PublicationGeneration = generation.Value,
            ClientVersion = existing.Manifest.ClientVersion,
        };

        byte[] encoded;
        using (var signer = RepositorySigner.Create(repository.Hierarchy, generation))
        {
            encoded = SnapshotManifestCodec.Encode(
                snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
        }

        var builder = new ManifestBuilder(
            repository.RepositoryId, WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            generation, repository.Keys, store, new MonotonicBlobCounterAllocator(9000), SpoolDirectory,
            BlobWriteProfile.LocalDefault);

        await using (builder.ConfigureAwait(false))
        {
            await builder.WriteStandaloneSnapshotAsync(
                snapshot, encoded, intentSequence: 9001, CancellationToken.None);
        }

        return capturedAt;
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    public void Dispose()
    {
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
