using Bodu;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Restore;

/// <summary>
/// What a restore as a whole achieved (FR-RST-004; architecture 08 §3).
/// </summary>
/// <remarks>
/// A boolean cannot carry this. It was one, computed as "nothing failed",
/// which made a restore that skipped every symlink and special file report
/// itself complete — while architecture 08 §3 says a restore that recovered
/// 9 999 of 10 000 files is a failed restore that recovered 9 999 files.
/// </remarks>
public enum RestoreOutcome
{
    /// <summary>Every required item was restored and verified.</summary>
    Complete = 0,

    /// <summary>Nothing failed, but a required item was not materialised.</summary>
    Partial = 1,

    /// <summary>At least one required item could not be restored.</summary>
    Failed = 2,

    /// <summary>The operator stopped it.</summary>
    Cancelled = 3,
}

/// <summary>
/// Where restored historical content lands (FR-RST-006; architecture 08 §3.1).
/// </summary>
/// <remarks>
/// A snapshot may contain malware that was present when it was captured, and
/// restoring it is what the user asked for. Restoring it *into the live tree
/// by default* is not — that is a decision a person should make deliberately,
/// not one that happens because they pressed Enter.
/// </remarks>
public enum RestoreDestinationMode
{
    /// <summary>
    /// Under a quarantine directory inside the chosen output, so nothing
    /// unscanned lands where the system will run it. The default.
    /// </summary>
    Quarantine = 0,

    /// <summary>Directly into the chosen output — the operator asked for this explicitly.</summary>
    InPlace = 1,
}

/// <summary>What to do about a file already sitting at a restore destination.</summary>
/// <remarks>
/// <b>This is not architecture 08 §3.1's quarantine control</b>, and the two
/// were conflated. §3.1 is about where *restored historical content* lands —
/// unscanned content from an old snapshot should not be dropped into a live
/// tree by default. This is about what happens to a file that is already
/// there, which is a different question with a different answer.
/// </remarks>
public enum ExistingDestinationPolicy
{
    /// <summary>Move it aside into the displaced store, then restore. The default.</summary>
    Preserve = 0,

    /// <summary>Leave it, and record the item as failed.</summary>
    Fail = 1,

    /// <summary>Overwrite it. Destructive, and never the default.</summary>
    Replace = 2,
}

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
    /// <remarks>
    /// Version 2 replaced the <c>complete</c> boolean with <c>outcome</c>.
    /// The boolean is not carried alongside it: a reader that understood the
    /// old field would have read <c>true</c> for a partial restore, which is
    /// the defect, so leaving it present would preserve the lie for exactly
    /// the readers most likely to trust it.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>Paths whose existing file was moved aside to make room.</summary>
    [JsonPropertyName("displaced")]
    public required IReadOnlyList<string> Displaced { get; init; }

    /// <summary>
    /// Where content was actually written — the quarantine directory, or the
    /// operator's chosen output when they asked for in place.
    /// </summary>
    /// <remarks>
    /// Recorded because "restored to /home/ana/recovered" and "restored to
    /// /home/ana/recovered/.fbp-quarantine/&lt;run&gt;" are different claims,
    /// and a receipt that cannot tell them apart cannot answer the only
    /// question that matters after a restore: where are my files.
    /// </remarks>
    [JsonPropertyName("written_to")]
    public required string WrittenTo { get; init; }

    /// <summary>What the restore as a whole achieved.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RestoreOutcome>))]
    [JsonPropertyName("outcome")]
    public required RestoreOutcome Outcome { get; init; }

    /// <summary>The receipt as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

/// <summary>Executor switches.</summary>
public sealed record RestoreExecutionOptions
{
    /// <summary>Where restored content lands (FR-RST-006).</summary>
    /// <remarks>
    /// Defaults to <see cref="RestoreDestinationMode.Quarantine"/>. This is a
    /// different control from <see cref="ExistingDestination"/> and the two
    /// were once conflated: this one is about where the restored file goes,
    /// that one is about what happens to a file already there.
    /// </remarks>
    public RestoreDestinationMode DestinationMode { get; init; } = RestoreDestinationMode.Quarantine;

    /// <summary>The quarantine directory, relative to the chosen output.</summary>
    public string QuarantineDirectory { get; init; } = ".fbp-quarantine";

    /// <summary>What to do about a file already at a destination.</summary>
    /// <remarks>
    /// Defaults to <see cref="ExistingDestinationPolicy.Preserve"/>: restore
    /// never destroys what it found.
    /// </remarks>
    public ExistingDestinationPolicy ExistingDestination { get; init; } = ExistingDestinationPolicy.Preserve;

    /// <summary>
    /// The directory displaced files are moved into, relative to the restore
    /// root. Each run gets its own subdirectory, so two restores of the same
    /// path cannot overwrite one another's displaced copies.
    /// </summary>
    public string DisplacedDirectory { get; init; } = ".fbp-displaced";

    /// <summary>
    /// Distinguishes this run's displaced store from every other run's.
    /// </summary>
    /// <remarks>
    /// A single shared refuge with <c>overwrite: true</c> was worse than no
    /// refuge: restoring the same path twice destroyed the first displaced
    /// copy silently, which is precisely the data the policy exists to keep.
    /// </remarks>
    public required string RunId { get; init; }

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
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNullOrWhiteSpace(outputDirectory);
        ThrowHelper.ThrowIfNull(options);

        using var activity = FallbackPlan.Domain.Diagnostics.EngineDiagnostics.Activities.StartActivity("restore");
        Directory.CreateDirectory(outputDirectory);

        // Resolved once, and every destination is checked against it. A
        // repository is not a trusted source of path text: the threat model
        // treats other participants and historical data as adversarial, so a
        // manifest naming "../../.ssh/authorized_keys" must be refused by
        // construction rather than by whoever wrote the store.
        var chosen = new DirectoryInfo(outputDirectory).FullName;

        // Quarantine by default (08 §3.1): restored historical content lands
        // under a directory of its own rather than directly where the operator
        // pointed, unless they said in place. Displaced files stay beside the
        // chosen output, not inside the quarantine — they are the user's
        // current files and were never in question.
        var root = options.DestinationMode == RestoreDestinationMode.Quarantine
            ? Path.Combine(chosen, options.QuarantineDirectory, options.RunId)
            : chosen;

        Directory.CreateDirectory(root);
        var displacedRoot = Path.Combine(chosen, options.DisplacedDirectory, options.RunId);
        var engine = new RestoreEngine(reader);
        var items = new List<ReceiptItem>();
        var displaced = new List<string>();

        foreach (var item in plan.Items)
        {
            // Cooperative stop, not an exception: break so the receipt is still
            // produced and Aggregate can see fewer items than planned and
            // report Cancelled (architecture 08 §3's outcome, previously
            // unreachable because this threw before the receipt was built).
            // Stopping between items means every item already in the receipt is
            // whole — a cancelled restore leaves the same class of state a
            // completed prefix would.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!TryResolve(root, item.Path, out var destination, out var refusal))
            {
                items.Add(new ReceiptItem
                {
                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = refusal,
                });
                continue;
            }

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

                    if (File.Exists(destination))
                    {
                        if (options.ExistingDestination == ExistingDestinationPolicy.Fail)
                        {
                            File.Delete(spool);
                            items.Add(new ReceiptItem
                            {
                                Path = item.Path, Outcome = "failed", Bytes = 0,
                                Detail = "a file is already at this destination and the policy is to fail",
                            });
                            continue;
                        }

                        if (options.ExistingDestination == ExistingDestinationPolicy.Preserve)
                        {
                            // Moved aside before the verified version lands, into
                            // this run's own store. overwrite is false on purpose:
                            // if something is already there the displaced copy is
                            // not ours to destroy.
                            if (!TryResolve(displacedRoot, item.Path, out var refuge, out var displacedRefusal))
                            {
                                File.Delete(spool);
                                items.Add(new ReceiptItem
                                {
                                    Path = item.Path, Outcome = "failed", Bytes = 0, Detail = displacedRefusal,
                                });
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(refuge)!);
                            File.Move(destination, refuge);
                            displaced.Add(item.Path);
                        }
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
            Displaced = displaced,
            WrittenTo = root,
            Outcome = Aggregate(items, plan.Items.Count),
        };
    }

    /// <summary>Reduces the item outcomes to what the restore as a whole achieved.</summary>
    /// <param name="items">Every item's outcome.</param>
    /// <param name="planned">How many items the plan held.</param>
    /// <returns>The aggregate.</returns>
    /// <remarks>
    /// A skipped item makes the restore <see cref="RestoreOutcome.Partial"/>.
    /// The plan declaring in advance that a symlink cannot be materialised on
    /// this target is a reason the shortfall is expected — it is not a reason
    /// to report that nothing is missing. What the operator needs to know is
    /// that the tree they restored is not the tree that was captured.
    /// </remarks>
    private static RestoreOutcome Aggregate(List<ReceiptItem> items, int planned)
    {
        if (items.Any(item => item.Outcome == "failed"))
        {
            return RestoreOutcome.Failed;
        }

        if (items.Count != planned)
        {
            return RestoreOutcome.Cancelled;
        }

        return items.Any(item => item.Outcome == "skipped")
            ? RestoreOutcome.Partial
            : RestoreOutcome.Complete;
    }

    /// <summary>
    /// Resolves one repository path under <paramref name="root"/>, refusing
    /// anything that is not a sequence of plain name components.
    /// </summary>
    /// <param name="root">The already-resolved containment root.</param>
    /// <param name="path">The repository-supplied path. Untrusted.</param>
    /// <param name="destination">The resolved destination when this returns true.</param>
    /// <param name="refusal">Why it was refused, for the receipt.</param>
    /// <returns><see langword="true"/> when the path is safe to materialise.</returns>
    /// <remarks>
    /// Component validation is the boundary; the containment check underneath
    /// it is defence in depth, not the mechanism. A check that only compares
    /// the combined string against a prefix is defeated by anything the
    /// platform normalises differently from the checker — which is most of the
    /// interesting cases.
    /// </remarks>
    private static bool TryResolve(
        string root, string path, out string destination, out string refusal)
    {
        destination = string.Empty;
        refusal = string.Empty;

        if (path.Length == 0)
        {
            refusal = "refused: the manifest names an empty path";
            return false;
        }

        var built = root;
        foreach (var component in path.Split('/'))
        {
            if (!IsPlainComponent(component, out var why))
            {
                refusal = $"refused: {why}";
                return false;
            }

            built = Path.Combine(built, component);
        }

        var resolved = Path.GetFullPath(built);
        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(fence, StringComparison.Ordinal))
        {
            refusal = "refused: the path resolves outside the restore root";
            return false;
        }

        // GetFullPath is lexical — it normalises `..` and `.` but does not
        // follow symlinks. A link inside the root pointing out of it (seeded by
        // an attacker, or restored by an earlier item) would let a later path
        // traverse it and escape though every component reads as plain. Resolve
        // the deepest existing ancestor through its links and re-check: a real
        // path that leaves the root is refused before anything is written.
        if (EscapesThroughALink(root, resolved))
        {
            refusal = "refused: the path traverses a symlink that leaves the restore root";
            return false;
        }

        destination = resolved;
        return true;
    }

    private static bool EscapesThroughALink(string root, string resolved)
    {
        // Lexical containment already proved `resolved` is under `root`. What is
        // left is a component *between* the root and the destination that exists
        // on disk as a symlink leaving the root — the only way a link can be
        // traversed by this write. The root itself is the executor's own
        // directory and is not suspect; anything at or above it is out of scope.
        var realRootFence = RealPath(root) + Path.DirectorySeparatorChar;
        var rootFence = (root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);

        var component = Path.GetDirectoryName(resolved);
        while (!string.IsNullOrEmpty(component)
            && component.Length >= rootFence.Length
            && (component + Path.DirectorySeparatorChar).StartsWith(rootFence, StringComparison.Ordinal))
        {
            if (Directory.Exists(component) || File.Exists(component))
            {
                var real = RealPath(component) + Path.DirectorySeparatorChar;
                if (!real.StartsWith(realRootFence, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            component = Path.GetDirectoryName(component);
        }

        return false;
    }

    private static string RealPath(string path)
    {
        // ResolveLinkTarget with returnFinalTarget follows a chain of links to
        // the real object; a non-link returns null and the path stands.
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            return resolved is null ? Path.GetFullPath(path) : Path.GetFullPath(resolved);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsPlainComponent(string component, out string why)
    {
        why = string.Empty;

        if (component.Length == 0)
        {
            why = "the manifest path has an empty component";
            return false;
        }

        if (component is "." or "..")
        {
            why = $"the manifest path contains a '{component}' component";
            return false;
        }

        if (component.Contains('\0', StringComparison.Ordinal))
        {
            why = "the manifest path contains a NUL";
            return false;
        }

        // Separators, drive-relative and UNC forms cannot appear inside one
        // component. A backslash is a separator on Windows and a legal
        // filename byte on POSIX, so it is refused on both: a name that means
        // two different shapes on two platforms is one this cannot restore
        // predictably on either.
        if (component.Contains('/', StringComparison.Ordinal)
            || component.Contains('\\', StringComparison.Ordinal)
            || component.Contains(':', StringComparison.Ordinal))
        {
            why = "the manifest path component contains a separator or drive marker";
            return false;
        }

        if (Path.IsPathRooted(component))
        {
            why = "the manifest path component is rooted";
            return false;
        }

        return true;
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
