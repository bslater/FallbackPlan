using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using System.Runtime.Versioning;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What an operator is told when a backup captured most of what it was asked
/// for and not all of it (NFR-OPS-002, FR-SNP-007).
/// </summary>
/// <remarks>
/// <para>
/// The surveyed changelog records three separate shipped fixes here: an
/// operation reporting success although it failed, one error masking another
/// during a backup, and compression errors being hidden. Three releases, one
/// subject: the engine knew something went wrong and the operator did not.
/// </para>
/// <para>
/// This is the worst class of backup defect, worse than a crash, because a
/// crash gets investigated. A backup that says it succeeded and did not is
/// discovered at restore, which is the one moment when there is no time left
/// to do anything about it.
/// </para>
/// <para>
/// The repository half is already proven elsewhere —
/// <c>SnapshotPublicationTests</c> shows every failure reaching the error
/// manifest and <c>capture_status</c> going to 2. What is tested here is the
/// half an operator actually looks at: the job in the journal and the line the
/// agent prints.
/// </para>
/// </remarks>
[TestClass]
public sealed class PartialBackupHonestyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-partial-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "partial-backup-tests-passphrase!!";

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, new string('a', 32));

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    public PartialBackupHonestyTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "readable.txt"), "this one is fine");
    }

    private async Task CreateRepositoryAsync()
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        using var _ = await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase,
            Domain.Configuration.RepositoryCreationSettings.Default,
            createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32),
                Name = "vault",
                Kind = DestinationKind.LocalPath,
                Path = Path.Combine(StateDirectory, "vault"),
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Root = SourceRoot,
                Schedule = "every 4h",
                Destinations = [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private async Task<AgentPassResult> RunPassAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        return await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
    }

    /// <summary>Makes one file genuinely unreadable, by removing every mode bit.</summary>
    [UnsupportedOSPlatform("windows")]
    private string WriteUnreadableFile(string name)
    {
        var path = Path.Combine(SourceRoot, name);
        File.WriteAllText(path, "this one is not");
        File.SetUnixFileMode(path, UnixFileMode.None);
        return path;
    }

    /// <summary>
    /// The central question, asked of the surface an operator reads: after a
    /// backup that could not read one of its files, is that discoverable
    /// without parsing prose?
    /// </summary>
    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix,
        "denial is expressed with chmod, and root can read a 000 file regardless")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task ABackupThatCouldNotReadAFile_IsDistinguishableFromACleanOne()
    {
        await CreateRepositoryAsync();
        WriteConfiguration();
        WriteUnreadableFile("denied.txt");

        var result = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.AreEqual("ran", Assert.ContainsSingle(result.Sets).Outcome);

        var job = Assert.ContainsSingle(JobStateStore.Open(StateDirectory).Jobs);
        Assert.IsNotNull(job.SnapshotId, "the snapshot is still committed — a partial backup is a backup");

        // The state carries it, not the prose. Spec 02 §8 makes the same
        // argument about wire refusals — the code is normative and the message
        // is not for parsing — and a job state a console or a script reads is
        // the same kind of surface.
        Assert.AreEqual(JobState.CompletedWithFailures, job.State);

        // The detail is still there and still says how many, because a count
        // is the thing a person wants next. It is simply no longer the only
        // difference between this and a clean run.
        Assert.IsNotNull(job.Detail);
        Assert.Contains("partial", job.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The clean run, so the test above is read as a comparison rather than as
    /// an assertion about `Complete` in general.
    /// </summary>
    [TestMethod]
    public async Task ABackupThatReadEverything_CompletesWithNothingToReport()
    {
        await CreateRepositoryAsync();
        WriteConfiguration();

        var result = await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.AreEqual("ran", Assert.ContainsSingle(result.Sets).Outcome);

        var job = Assert.ContainsSingle(JobStateStore.Open(StateDirectory).Jobs);
        Assert.AreEqual(JobState.Complete, job.State);
        Assert.IsNull(job.Detail, "a clean backup should have nothing to say");
    }

    /// <summary>
    /// The structured form of the same question, and the one that should be
    /// answering it: a job state that says a backup was partial, so a script
    /// or a console can act on it without reading English.
    /// </summary>
    /// <remarks>
    /// Asserted against the vocabulary rather than against a run, and
    /// deliberately so. The permission-based tests in this class need a
    /// non-root user — root reads a 000 file regardless — so they skip in a
    /// container and prove nothing there. The vocabulary is decidable
    /// anywhere, which is what let this finding be confirmed and then closed
    /// on a machine that cannot run the other four.
    /// </remarks>
    [TestMethod]
    public void TheJobVocabulary_HasATerminalStateForAPartialCapture()
    {
        var terminal = Enum.GetNames<JobState>();

        Assert.Contains(
            name => name.Contains("Partial", StringComparison.Ordinal)
                || name.Contains("Failures", StringComparison.Ordinal)
                || name.Contains("Degraded", StringComparison.Ordinal),
            terminal,
            $"no state distinguishes a partial capture from a clean one: {string.Join(", ", terminal)}");
    }

    /// <summary>
    /// Several failures must all survive to the operator — the "one error
    /// masking another" class. Two unreadable files must be counted as two.
    /// </summary>
    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "denial is expressed with chmod")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task SeveralFilesFail_EveryOneOfThemIsCounted()
    {
        await CreateRepositoryAsync();
        WriteConfiguration();
        WriteUnreadableFile("denied-one.txt");
        WriteUnreadableFile("denied-two.txt");
        WriteUnreadableFile("denied-three.txt");

        await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));

        var job = Assert.ContainsSingle(JobStateStore.Open(StateDirectory).Jobs);

        Assert.IsNotNull(job.Detail);
        Assert.Contains("3 failure", job.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the readable files are still backed up. Refusing the whole set
    /// because one file is unreadable would be the opposite failure, and a
    /// worse one.
    /// </summary>
    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "denial is expressed with chmod")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task OneFileFails_TheRestAreStillCaptured()
    {
        await CreateRepositoryAsync();
        WriteConfiguration();
        WriteUnreadableFile("denied.txt");

        await RunPassAsync(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));

        using var passphrase = Passphrase.Create(PassphraseText);
        using var repository = await RepositoryLifecycle.OpenAsync(
            new LocalFileSystemObjectStore(RepoPath), passphrase, CancellationToken.None);
        using var catalogue = Repository.Catalogue.Catalogue.Open(
            Path.Combine(StateDirectory, $"catalogue-{repository.RepositoryId}.db"), repository.RepositoryId);

        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        var entries = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);

        Assert.Contains(entry => entry.Path == "readable.txt", entries);
        Assert.DoesNotContain(entry => entry.Path == "denied.txt", entries);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // The 000-mode files have to be made deletable again first. Nothing
        // was chmodded on Windows, so nothing needs restoring there.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_root, recursive: true);
    }
}
