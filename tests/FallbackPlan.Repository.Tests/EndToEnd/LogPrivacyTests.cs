using FallbackPlan.Domain;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// NFR-SEC-006 and NFR-PRIV-003 for the logging channel, and the direct
/// analogue of <see cref="TelemetryPrivacyTests"/>: a recording logger runs
/// through a full publish and restore, and every captured record is rendered
/// both ways.
/// </summary>
/// <remarks>
/// <para>
/// The two halves matter equally. The <b>redacted</b> rendering must contain no
/// plaintext path and no secret-bearing value — that is what makes it safe to
/// hand a paired console a log. The <b>full</b> rendering must contain the path
/// — that is what makes the first assertion mean something. Without the second
/// half this suite would pass perfectly the day somebody stopped logging paths
/// at all, which is how a privacy test becomes theatre.
/// </para>
/// <para>
/// Redaction is by declared type, never by pattern (ADR-0043 §4). Nothing here
/// greps for things that look like secrets; it renders what the engine actually
/// logged and asserts on the result.
/// </para>
/// </remarks>
[TestClass]
public sealed class LogPrivacyTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    /// <summary>
    /// A path segment distinctive enough that finding it in redacted output is
    /// unambiguous — no engine string could contain it by accident.
    /// </summary>
    private const string TellingName = "quarterly-redundancies-confidential";

    private async Task<RecordingLogger> PublishAndRestoreAsync()
    {
        var log = new RecordingLogger();

        var source = new FakeFileSystemSource();
        source.AddFile(
            $"{TellingName}/alpha.bin", [.. Enumerable.Range(0, 100_000).Select(i => (byte)i)]);
        source.AddFile(
            $"{TellingName}/copy.bin", [.. Enumerable.Range(0, 100_000).Select(i => (byte)i)]);
        source.AddFile($"{TellingName}/broken.bin", [1, 2, 3]).OpenFailure =
            new UnauthorizedAccessException("denied");

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue, progress: null, logger: log);

        var snapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray();
        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = snapshotId,
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "log-privacy-tests/1.0",
            },
            CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        await new RestoreExecutor(reader, target, log).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "restored"),
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_600_000_001 },
            CancellationToken.None);

        return log;
    }

    private static LogRecord ToRecord(RecordedLog recorded) => new(
        Sequence: 0,
        TimestampUnixMilliseconds: 0,
        Level: recorded.Level,
        EventId: recorded.EventId,
        Category: "test",
        Values: recorded.Values,
        ExceptionType: recorded.Exception?.GetType().FullName,
        ExceptionMessage: recorded.Exception?.Message);

    [TestMethod]
    public async Task Logging_AFullPublishAndRestore_PutsNoPlaintextPathInTheRedactedRendering()
    {
        var log = await PublishAndRestoreAsync();

        Assert.IsNotEmpty(log.Records, "The pass logged nothing, so this suite would prove nothing.");

        foreach (var recorded in log.Records)
        {
            var redacted = LogRecordRenderer.Render(ToRecord(recorded), RenderMode.Redacted);

            Assert.DoesNotContain(
                TellingName, redacted, StringComparison.Ordinal,
                $"Event {recorded.EventId} put a plaintext path across the boundary: {redacted}");
        }
    }

    [TestMethod]
    public async Task Logging_TheSameRecords_DoCarryThePathInTheFullRendering()
    {
        // The half that stops the suite passing vacuously. If the engine ever
        // stops logging paths, this fails and the other test keeps passing —
        // which is the point of asserting both directions.
        var log = await PublishAndRestoreAsync();

        var full = log.Records
            .Select(recorded => LogRecordRenderer.Render(ToRecord(recorded), RenderMode.Full))
            .ToArray();

        Assert.IsNotEmpty(
            full.Where(line => line.Contains(TellingName, StringComparison.Ordinal)),
            "No record carried a path at all, so the redaction assertion proves nothing. "
            + "Either the engine stopped logging paths or the call sites were lost.");
    }

    [TestMethod]
    public async Task Logging_EveryLoggedValue_IsEitherRedactableOrHarmless()
    {
        // Redaction is by declared type. A path that arrived as a bare string
        // would render identically in both modes and slip through the test
        // above only because it did not happen to contain the telling name —
        // so the types themselves are checked.
        var log = await PublishAndRestoreAsync();
        var offenders = new List<string>();

        foreach (var recorded in log.Records)
        {
            foreach (var (name, value) in recorded.Values)
            {
                if (name == LogRecord.OriginalFormatKey || value is null)
                {
                    continue;
                }

                // Numbers of every width, and the two non-numeric primitives
                // that cannot carry a path or a secret. Listed exhaustively
                // rather than by "is it a value type": a struct wrapping a
                // path is a value type too, and the point of this test is that
                // anything it cannot classify has to be looked at.
                if (value is IRedactedValue or bool or Enum
                    or byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal)
                {
                    continue;
                }

                // A string is allowed only when it is not path-shaped: reasons,
                // outcomes and policy names are strings by nature.
                if (value is string text &&
                    !text.Contains('/', StringComparison.Ordinal) &&
                    !text.Contains('\\', StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"event {recorded.EventId}, hole {{{name}}} = '{value}' ({value.GetType().Name})");
            }
        }

        Assert.IsEmpty(
            offenders,
            "These values are neither a redactable type nor plainly harmless, so redaction cannot "
            + $"reach them (NFR-SEC-006): {string.Join("; ", offenders)}");
    }

    [TestMethod]
    public async Task Logging_UnderAHostileCulture_RendersTheSameFacts()
    {
        // CA1305 is an error in this build and the renderer formats, so the
        // culture that breaks naive formatting gets its own pass.
        using (new CultureScope("tr-TR"))
        {
            var log = await PublishAndRestoreAsync();

            foreach (var recorded in log.Records)
            {
                Assert.DoesNotContain(
                    TellingName,
                    LogRecordRenderer.Render(ToRecord(recorded), RenderMode.Redacted),
                    StringComparison.Ordinal);
            }
        }
    }
}
