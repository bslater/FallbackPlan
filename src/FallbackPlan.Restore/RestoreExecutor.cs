using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Restore;

/// <summary>One item's outcome in the receipt.</summary>
public sealed record ReceiptItem
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("bytes")]
    public required ulong Bytes { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// The machine-readable restore receipt (FR-RST-004/005; ADR-0026
/// §Decision 10): a versioned client-domain JSON document accounting for
/// <b>every planned item</b> — restored, skipped, or failed, with the
/// quarantine ledger — so "the restore succeeded" is a checkable claim,
/// never an impression.
/// </summary>
public sealed record RestoreReceipt
{
    /// <summary>The current receipt schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("started_at")]
    public required ulong StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public required ulong CompletedAt { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<ReceiptItem> Items { get; init; }

    [JsonPropertyName("quarantined")]
    public required IReadOnlyList<string> Quarantined { get; init; }

    /// <summary>True iff every planned item restored and verified.</summary>
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }

    /// <summary>The receipt as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

/// <summary>Executor switches.</summary>
public sealed record RestoreExecutionOptions
{
    /// <summary>
    /// Quarantine-by-default (architecture 08 §3): an existing file at a
    /// destination path is moved into the quarantine directory before the
    /// restored version lands — restore never destroys what it found.
    /// </summary>
    public bool QuarantineExisting { get; init; } = true;

    /// <summary>The wall clock for receipt timestamps.</summary>
    public required ulong NowUnixMilliseconds { get; init; }
}

/// <summary>
/// Executes a restore plan: content first through the verifying engine
/// (per-segment content identifiers plus the whole-file hash before any
/// file reaches its destination), metadata strictly after content, and a
/// receipt that accounts for everything (FR-RST-004/005).
/// </summary>
public sealed class RestoreExecutor(RepositoryReader reader, RestoreTargetProfile target)
{
    /// <summary>Runs <paramref name="plan"/> into <paramref name="outputDirectory"/>.</summary>
    public async ValueTask<RestoreReceipt> ExecuteAsync(
        RestorePlan plan,
        string outputDirectory,
        RestoreExecutionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(outputDirectory);
        var quarantineRoot = Path.Combine(outputDirectory, ".fbp-quarantine");
        var engine = new RestoreEngine(reader);
        var items = new List<ReceiptItem>();
        var quarantined = new List<string>();

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(outputDirectory, item.Path.Replace('/', Path.DirectorySeparatorChar));

            if (item.Kind == EntryKind.DirectoryPlaceholder)
            {
                Directory.CreateDirectory(destination);
                items.Add(new ReceiptItem { Path = item.Path, Outcome = "restored", Bytes = 0 });
                continue;
            }

            var read = await reader.ReadSegmentAsync(item.ObjectId, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                items.Add(new ReceiptItem
                {
                    Path = item.Path, Outcome = "failed", Bytes = 0,
                    Detail = $"manifest read {read.Outcome}",
                });
                continue;
            }

            var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            switch (manifest.EntryKind)
            {
                case EntryKind.File:
                {
                    var spool = destination + ".fbp-restore-tmp";
                    RestoreResult result;
                    var output = File.Create(spool);
                    await using (output.ConfigureAwait(false))
                    {
                        result = await engine.RestoreFileAsync(manifest, output, cancellationToken).ConfigureAwait(false);
                    }

                    if (!result.Success)
                    {
                        File.Delete(spool);
                        items.Add(new ReceiptItem
                        {
                            Path = item.Path, Outcome = "failed", Bytes = 0, Detail = result.FailureDetail,
                        });
                        continue;
                    }

                    // Quarantine before the verified version lands — what
                    // was there is preserved, never destroyed (08 §3).
                    if (File.Exists(destination) && options.QuarantineExisting)
                    {
                        var refuge = Path.Combine(
                            quarantineRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(refuge)!);
                        File.Move(destination, refuge, overwrite: true);
                        quarantined.Add(item.Path);
                    }

                    File.Move(spool, destination, overwrite: true);

                    // Metadata strictly AFTER content (architecture 08 §3):
                    // a crash between the two leaves verified content with
                    // default metadata — recoverable — never the reverse.
                    ApplyMetadata(destination, manifest.Metadata);

                    items.Add(new ReceiptItem { Path = item.Path, Outcome = "restored", Bytes = (ulong)result.Length });
                    break;
                }

                case EntryKind.Symlink when target.SupportsSymlinks && manifest.LinkTarget is { } linkTarget:
                {
                    var targetText = System.Text.Encoding.UTF8.GetString(linkTarget.Span);
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    File.CreateSymbolicLink(destination, targetText);
                    items.Add(new ReceiptItem { Path = item.Path, Outcome = "restored", Bytes = 0 });
                    break;
                }

                case EntryKind.Symlink:
                    items.Add(new ReceiptItem
                    {
                        Path = item.Path, Outcome = "skipped", Bytes = 0,
                        Detail = "symlinks are not supported on this target (declared in the plan)",
                    });
                    break;

                default:
                    items.Add(new ReceiptItem
                    {
                        Path = item.Path, Outcome = "skipped", Bytes = 0,
                        Detail = $"{manifest.EntryKind} is recorded but not materialised (declared in the plan)",
                    });
                    break;
            }
        }

        return new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = Convert.ToHexString(plan.SnapshotId.Span).ToLowerInvariant(),
            StartedAt = options.NowUnixMilliseconds,
            CompletedAt = options.NowUnixMilliseconds,
            Items = items,
            Quarantined = quarantined,
            Complete = items.All(item => item.Outcome != "failed") && items.Count == plan.Items.Count,
        };
    }

    private void ApplyMetadata(string destination, EntryMetadata metadata)
    {
        if (metadata.ModifiedAt is { } modified)
        {
            File.SetLastWriteTimeUtc(destination, DateTimeOffset.FromUnixTimeMilliseconds((long)modified).UtcDateTime);
        }

        if (target.SupportsPosixMetadata && metadata.PosixMode is { } mode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, (UnixFileMode)(mode & 0xFFF));
        }
    }
}
