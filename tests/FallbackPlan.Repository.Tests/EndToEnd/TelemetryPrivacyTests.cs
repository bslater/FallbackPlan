using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The NFR-PRIV-002 assertion ADR-0027 §3 promises, and NFR-SEC-006's
/// telemetry half: a listener captures
/// every measurement the engine emits through a full publish + restore,
/// and every attribute is checked against the exhaustive allowlist — no
/// path, filename, or identifier can ride telemetry, by test rather than
/// by review. The same run proves the headline instruments actually fire.
/// </summary>
[TestClass]
public sealed class TelemetryPrivacyTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static readonly Dictionary<string, string[]> AllowedValues =
        new(StringComparer.Ordinal)
        {
            ["outcome"] = ["captured", "reused", "failed"],
            ["reused"] = ["true", "false"],
            ["class"] = ["data", "meta"],
            ["operation"] = ["put", "get", "list", "delete"],
        };

    [TestMethod]
    public async Task Telemetry_EveryEmittedAttribute_IsOnTheAllowlistAndTheInstrumentsFire()
    {
        var captured = new ConcurrentBag<(string Instrument, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EngineDiagnostics.Name)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            captured.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            captured.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        // A full engine pass: publish a tree (with reuse and a failure),
        // then restore it — every emitter runs.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/alpha.bin", [.. Enumerable.Range(0, 100_000).Select(i => (byte)i)]);
        source.AddFile("docs/copy.bin", [.. Enumerable.Range(0, 100_000).Select(i => (byte)i)]);
        source.AddFile("broken.bin", [1, 2, 3]).OpenFailure = new UnauthorizedAccessException("denied");

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue);

        var snapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray();
        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                RootPath = "/",
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = snapshotId,
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "telemetry-tests/1.0",
            },
            CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "restored"),
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_600_000_001 }, CancellationToken.None);

        listener.Dispose();

        Assert.IsNotEmpty(captured);

        // THE assertion: every attribute name AND value comes from the
        // closed sets in ADR-0027 §3. A path, a hex identifier, or a new
        // unreviewed attribute fails here.
        foreach (var (instrument, tags) in captured)
        {
            foreach (var tag in tags)
            {
                Assert.IsTrue(
                    EngineDiagnostics.AllowedAttributes.Contains(tag.Key),
                    $"{instrument} emitted attribute '{tag.Key}' — not on the NFR-PRIV-002 allowlist.");
                Assert.IsTrue(
                    AllowedValues[tag.Key].Contains(tag.Value as string),
                    $"{instrument} emitted {tag.Key}='{tag.Value}' — outside the closed value set.");
            }
        }

        // The headline instruments all fired during the pass.
        var instruments = captured.Select(measurement => measurement.Instrument).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "fallbackplan.scan.files", "fallbackplan.scan.bytes",
                     "fallbackplan.archive.bytes_logical", "fallbackplan.archive.bytes_stored",
                     "fallbackplan.archive.segments", "fallbackplan.blob.sealed",
                     "fallbackplan.blob.fill_fraction", "fallbackplan.store.requests",
                     "fallbackplan.catalogue.lookup_duration", "fallbackplan.publication.duration",
                 })
        {
            Assert.Contains(expected, instruments);
        }

        // The dedup pair is coherent: the identical second file reused
        // every segment, so stored < logical is observable in the metrics.
        var scanOutcomes = captured
            .Where(measurement => measurement.Instrument == "fallbackplan.scan.files")
            .SelectMany(measurement => measurement.Tags)
            .Select(tag => tag.Value as string)
            .ToList();
        Assert.Contains("captured", scanOutcomes);
        Assert.Contains("failed", scanOutcomes);
    }
}
