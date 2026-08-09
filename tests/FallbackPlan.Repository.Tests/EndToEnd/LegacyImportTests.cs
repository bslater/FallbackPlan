using System.Runtime.CompilerServices;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Import.Abstractions;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// F6 / exit criterion 11 (FR-CP-002): a synthetic legacy source traverses
/// the SAME publication pipeline as a native backup — provenance lands only
/// in <c>capture_diagnostics</c>, the imported version restores
/// byte-identically, and stripped of key 13 its manifest is byte-identical
/// to what a native publication of the same bytes produces.
/// </summary>
[TestClass]
public sealed class LegacyImportTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    /// <summary>A legacy archive made of in-memory byte arrays — FR-CP-002's "arbitrary byte stream".</summary>
    private sealed class SyntheticLegacySource(params (string Name, byte[] Content, string LegacyId)[] versions)
        : ILegacyArchiveSource
    {
        public string SourceName => "synthetic-legacy";

        public async IAsyncEnumerable<LegacyFileVersion> EnumerateVersionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (name, content, legacyId) in versions)
            {
                yield return new LegacyFileVersion(
                    System.Text.Encoding.UTF8.GetBytes(name),
                    new ProvenanceRecord("synthetic-legacy", legacyId, 1_500_000_000_000, 1_600_000_000_000),
                    _ => ValueTask.FromResult<Stream>(new MemoryStream(content)));
            }

            await Task.CompletedTask;
        }
    }

    private PublicationOrchestrator CreateOrchestrator(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy) => new(
        SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
        new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
        SpoolDirectory);

    private static byte[] Content(int seed, int length = 700_000)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [TestMethod]
    public async Task LegacyImport_ASyntheticArchive_ImportsThroughTheNativePipelineAndRestores()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var first = Content(seed: 1);
        var second = Content(seed: 2);
        var source = new SyntheticLegacySource(
            ("report-v1.doc", first, "legacy:0001"),
            ("report-v2.doc", second, "legacy:0002"));

        var imported = await new LegacyImportPipeline(CreateOrchestrator(store, keys, hierarchy)).ImportAsync(
            source,
            deviceId: Enumerable.Repeat((byte)0x22, 16).ToArray(),
            backupSetId: Enumerable.Repeat((byte)0x33, 16).ToArray(),
            nowUnixMilliseconds: 1_722_600_000_000,
            expiryGeneration: 5,
            CancellationToken.None);

        Assert.AreEqual(2, imported.Count);

        // Each imported version restores byte-identically through the
        // ordinary read path — no import-specific reader exists to diverge.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        foreach (var (expected, version) in new[] { (first, imported[0]), (second, imported[1]) })
        {
            var manifestRead = await reader.ReadSegmentAsync(version.Published.FileVersionObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);
            var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            using var restored = new MemoryStream();
            var result = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
            Assert.IsTrue(result.Success, result.FailureDetail);
            SequenceAssert.AreEqual(expected, restored.ToArray());

            // Provenance rides in key 13 and nowhere else (FR-CP-002).
            Assert.Contains("imported-from: synthetic-legacy", manifest.CaptureDiagnostics);
            Assert.Contains($"legacy-id: {version.Provenance.SourceIdentifier}", manifest.CaptureDiagnostics);
        }
    }

    [TestMethod]
    public async Task LegacyImport_AManifestStrippedOfProvenance_IsByteIdenticalToANativeOne()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var content = Content(seed: 7);
        var orchestrator = CreateOrchestrator(store, keys, hierarchy);

        // Import the bytes, then publish the same bytes natively under the
        // same name into the same repository.
        var imported = await new LegacyImportPipeline(orchestrator).ImportAsync(
            new SyntheticLegacySource(("same.bin", content, "legacy:0042")),
            deviceId: Enumerable.Repeat((byte)0x22, 16).ToArray(),
            backupSetId: Enumerable.Repeat((byte)0x33, 16).ToArray(),
            nowUnixMilliseconds: 1_722_600_000_000,
            expiryGeneration: 5,
            CancellationToken.None);

        using var nativeSource = new MemoryStream(content);
        var native = await orchestrator.PublishAsync(
            new BackupJob(
                nativeSource,
                "same.bin"u8.ToArray(),
                Enumerable.Repeat((byte)0x22, 16).ToArray(),
                Enumerable.Repeat((byte)0x33, 16).ToArray(),
                Enumerable.Repeat((byte)0x44, 16).ToArray(),
                1_722_600_000_000,
                DeclaredMaxDurationMs: 3_600_000,
                ExpiryGeneration: 5,
                ClientVersion: "tests/1.0"),
            CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var importedRead = await reader.ReadSegmentAsync(imported[0].Published.FileVersionObjectId, CancellationToken.None);
        var nativeRead = await reader.ReadSegmentAsync(native.FileVersionObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, importedRead.Outcome);
        Assert.AreEqual(RecordReadOutcome.Ok, nativeRead.Outcome);

        var importedManifest = FileVersionManifestCodec.Decode(importedRead.Plaintext!);
        var nativeManifest = FileVersionManifestCodec.Decode(nativeRead.Plaintext!);

        // FR-CP-002's teeth: remove key 13 and the imported manifest encodes
        // to the native manifest's exact bytes — provenance is the ONLY
        // difference the format can see.
        Assert.IsNotEmpty(importedManifest.CaptureDiagnostics);
        Assert.IsEmpty(nativeManifest.CaptureDiagnostics);
        SequenceAssert.AreEqual(
            FileVersionManifestCodec.Encode(nativeManifest),
            FileVersionManifestCodec.Encode(importedManifest with { CaptureDiagnostics = [] }));
    }
}
