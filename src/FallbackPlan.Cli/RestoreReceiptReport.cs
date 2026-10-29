using System.Globalization;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Cli.Resources;
using FallbackPlan.Restore;

namespace FallbackPlan.Cli;

/// <summary>
/// What <c>restore-receipts</c> prints (FR-RST-004, ADR-0124): the restore
/// receipts a state directory keeps, by the service or in direct mode alike,
/// each run newest first with what it did and where it wrote, or one run's
/// receipt whole as it was kept. Through a paired service (ADR-0128), the
/// receipts it keeps, and one run's files it did not restore through the
/// passphrase.
/// </summary>
internal static class RestoreReceiptReport
{
    /// <summary>
    /// Writes the receipts kept under <paramref name="stateDirectory"/>, the
    /// newest <paramref name="limit"/> where one is given, and how many are kept.
    /// </summary>
    public static void WriteKept(TextWriter output, string stateDirectory, int? limit)
    {
        var directory = Path.Combine(stateDirectory, "receipts");
        var kept = RestoreReceipt.ReadKept(directory);
        if (kept.Count == 0)
        {
            output.WriteLine($"No restore receipt is kept under '{directory}'.");
            return;
        }

        IReadOnlyList<KeptRestoreReceipt> shown = limit is { } most ? [.. kept.Take(most)] : kept;
        foreach (var entry in shown)
        {
            if (entry.Receipt is not { } receipt)
            {
                output.WriteLine($"{entry.Run}  unreadable: {entry.Problem}");
                continue;
            }

            WriteHead(
                output,
                entry.Run,
                receipt.Outcome.ToString(),
                receipt.CompletedAt,
                receipt.SnapshotId,
                receipt.Set,
                Counts(
                    receipt.Items.Count(item => item.Outcome == "restored"),
                    receipt.Items.Count(item => item.FoundInPlace),
                    receipt.Items.Count(item => item.ResumedAt > 0),
                    receipt.Items.Count(item => item.Outcome == "skipped"),
                    receipt.Items.Count(item => item.Outcome == "failed"),
                    receipt.Items.Count(item => item.Outcome == "degraded")),
                receipt.WrittenTo);
        }

        output.WriteLine(shown.Count == kept.Count
            ? $"{kept.Count} restore {(kept.Count == 1 ? "receipt" : "receipts")} kept under '{directory}'."
            : $"The newest {shown.Count} of {kept.Count} restore receipts kept under '{directory}'.");
    }

    /// <summary>
    /// Writes the receipts kept under <paramref name="stateDirectory"/> as
    /// JSON (ADR-0141), the newest <paramref name="limit"/> where one is
    /// given, each as the service answers it (contract 1.73, ADR-0125), so a
    /// script reads a state directory's listing and a paired service's alike.
    /// </summary>
    public static void WriteKeptJson(TextWriter output, string stateDirectory, int? limit)
    {
        var kept = RestoreReceipt.ReadKept(Path.Combine(stateDirectory, "receipts"));
        var shown = limit is { } most ? kept.Take(most) : kept;
        output.WriteLine(JsonSerializer.Serialize(
            new RestoreReceiptsResult([.. shown.Select(Describe)], kept.Count), FrameCodec.SerializerOptions));
    }

    /// <summary>
    /// Writes the receipts a paired service keeps as JSON, as it answers them
    /// (ADR-0141), the newest <paramref name="limit"/> where one is given.
    /// </summary>
    /// <returns>The exit code.</returns>
    public static async Task<int> WriteServiceKeptJsonAsync(
        TextWriter output, IFallbackPlanClient client, int? limit, CancellationToken cancellationToken)
    {
        var listed = await AskAsync<RestoreReceiptsResult>(client, new ListRestoreReceiptsCommand(limit), cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(listed, FrameCodec.SerializerOptions));
        return 0;
    }

    /// <summary>
    /// Writes the receipt <paramref name="run"/> kept under
    /// <paramref name="stateDirectory"/>, whole and as it was kept.
    /// </summary>
    /// <exception cref="CliFailureException">When the name is not a run's, or the run kept no receipt there.</exception>
    public static void WriteRun(TextWriter output, string stateDirectory, string run)
    {
        // Checked before it is joined to a path: what is not a run's name
        // could name any file the state directory holds, or one outside it.
        RefuseUnlessARun(run);

        var directory = Path.Combine(stateDirectory, "receipts");
        var path = Path.Combine(directory, $"{run}.json");
        if (!File.Exists(path))
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_NoReceiptKept(run, directory));
        }

        output.WriteLine(File.ReadAllText(path).TrimEnd());
    }

    /// <summary>
    /// Lets the receipt <paramref name="run"/> kept under
    /// <paramref name="stateDirectory"/> go, and says so (ADR-0139).
    /// </summary>
    /// <exception cref="CliFailureException">When the name is not a run's, or the run kept no receipt there.</exception>
    public static void LetGo(TextWriter output, string stateDirectory, string run)
    {
        RefuseUnlessARun(run);

        var directory = Path.Combine(stateDirectory, "receipts");
        if (!RestoreReceipt.LetGo(directory, run))
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_NoReceiptKept(run, directory));
        }

        output.WriteLine($"let go the receipt of run {run} kept under '{directory}'");
    }

    /// <summary>
    /// Asks a paired service to let the receipt it keeps of
    /// <paramref name="run"/> go (contract 1.79, ADR-0140), and says so.
    /// </summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="CliFailureException">When the service keeps no receipt of the run, or refuses it.</exception>
    public static async Task<int> LetGoThroughServiceAsync(
        TextWriter output, IFallbackPlanClient client, string run, CancellationToken cancellationToken)
    {
        RefuseUnlessARun(run);
        _ = await AskAsync<AcknowledgedResult>(client, new ForgetRestoreReceiptCommand(run), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"let go the receipt of run {run} the service kept");
        return 0;
    }

    /// <summary>
    /// Lets every run's receipt kept under <paramref name="stateDirectory"/>
    /// go at once (ADR-0161), and says each and how many.
    /// </summary>
    /// <exception cref="CliFailureException">When one was not removed.</exception>
    public static void LetGoAll(TextWriter output, string stateDirectory)
    {
        var directory = Path.Combine(stateDirectory, "receipts");
        var outcomes = RestoreReceipt.LetGoAll(directory);
        if (outcomes.Count == 0)
        {
            output.WriteLine($"No restore receipt is kept under '{directory}'.");
            return;
        }

        foreach (var outcome in outcomes.Where(outcome => outcome.Problem is null))
        {
            output.WriteLine($"let go the receipt of run {outcome.Run} kept under '{directory}'");
        }

        output.WriteLine(LetGoCount(outcomes.Count(outcome => outcome.Problem is null), $"kept under '{directory}'"));
        RefuseUnlessAllLetGo([.. outcomes.Where(outcome => outcome.Problem is not null).Select(outcome => $"{outcome.Run}: {outcome.Problem}")]);
    }

    /// <summary>
    /// Asks a paired service to let every receipt it keeps go (ADR-0161): each
    /// it lists is let go by its run's name (contract 1.79, ADR-0140), and says
    /// each and how many.
    /// </summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="CliFailureException">When the service let one it listed not go.</exception>
    public static async Task<int> LetGoAllThroughServiceAsync(
        TextWriter output, IFallbackPlanClient client, CancellationToken cancellationToken)
    {
        var listed = await AskAsync<RestoreReceiptsResult>(client, new ListRestoreReceiptsCommand(), cancellationToken)
            .ConfigureAwait(false);
        if (listed.Receipts.Count == 0)
        {
            output.WriteLine("No restore receipt is kept by the service.");
            return 0;
        }

        var letGo = 0;
        var refused = new List<string>();
        foreach (var receipt in listed.Receipts)
        {
            try
            {
                _ = await AskAsync<AcknowledgedResult>(client, new ForgetRestoreReceiptCommand(receipt.Run), cancellationToken)
                    .ConfigureAwait(false);
                output.WriteLine($"let go the receipt of run {receipt.Run} the service kept");
                letGo++;
            }
            catch (CliFailureException refusal)
            {
                refused.Add($"{receipt.Run}: {refusal.Message}");
            }
        }

        output.WriteLine(LetGoCount(letGo, "the service kept"));
        RefuseUnlessAllLetGo(refused);
        return 0;
    }

    private static string LetGoCount(int letGo, string where) =>
        string.Create(CultureInfo.InvariantCulture, $"let go {letGo} restore {(letGo == 1 ? "receipt" : "receipts")} {where}");

    private static void RefuseUnlessAllLetGo(List<string> notLetGo)
    {
        if (notLetGo.Count > 0)
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_NotAllLetGo(notLetGo.Count, string.Join("; ", notLetGo)));
        }
    }

    /// <summary>Refuses what is not a run's name.</summary>
    /// <exception cref="CliFailureException">When it is not.</exception>
    public static void RefuseUnlessARun(string run)
    {
        if (!RestoreReceipt.IsRunName(run))
        {
            throw new CliFailureException(Strings.RestoreReceiptReport_NotARunsName);
        }
    }

    /// <summary>
    /// Writes the receipts a paired service keeps (ADR-0128), the newest
    /// <paramref name="limit"/> where one is given, as <see cref="WriteKept"/>
    /// writes a state directory's: none of the backup's files is named, so no
    /// passphrase is asked.
    /// </summary>
    /// <returns>The exit code.</returns>
    public static async Task<int> WriteServiceKeptAsync(
        TextWriter output, IFallbackPlanClient client, int? limit, CancellationToken cancellationToken)
    {
        var listed = await AskAsync<RestoreReceiptsResult>(client, new ListRestoreReceiptsCommand(limit), cancellationToken)
            .ConfigureAwait(false);
        if (listed.Total == 0)
        {
            output.WriteLine("No restore receipt is kept by the service.");
            return 0;
        }

        foreach (var receipt in listed.Receipts)
        {
            WriteHead(output, receipt);
        }

        output.WriteLine(listed.Receipts.Count == listed.Total
            ? $"{listed.Total} restore {(listed.Total == 1 ? "receipt" : "receipts")} kept by the service."
            : $"The newest {listed.Receipts.Count} of {listed.Total} restore receipts kept by the service.");
        return 0;
    }

    /// <summary>
    /// Writes what the receipt the service keeps of <paramref name="run"/>
    /// says, and the files its run did not restore, at most
    /// <paramref name="limit"/> of them, through a source of the set the
    /// receipt records: its paths are the backup's file names, which only that
    /// set's passphrase shows (FR-WOR-007). The source is closed after.
    /// </summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="CliFailureException">
    /// When the service keeps no receipt of the run, it does not read, it records no set, or the passphrase is
    /// not named or does not open that set.
    /// </exception>
    public static async Task<int> WriteServiceRunAsync(
        TextWriter output,
        IFallbackPlanClient client,
        string? passphraseEnvironmentVariable,
        string run,
        int? limit,
        CancellationToken cancellationToken)
    {
        // Which set the receipt records is the service's to say, and a receipt
        // whose set no passphrase is known to open is refused before one is asked.
        var listed = await AskAsync<RestoreReceiptsResult>(client, new ListRestoreReceiptsCommand(), cancellationToken)
            .ConfigureAwait(false);
        var named = listed.Receipts.FirstOrDefault(receipt => string.Equals(receipt.Run, run, StringComparison.Ordinal))
            ?? throw new CliFailureException(Strings.FormatRestoreReceiptReport_NoReceiptTheServiceKeeps(run));
        if (named.Problem is { } problem)
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_ReceiptDoesNotRead(run, problem));
        }

        if (named.SetId is not { } setId)
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_ReceiptRecordsNoSet(run));
        }

        var source = (await GrantedSources.OpenAsync(
            client,
            passphraseEnvironmentVariable,
            $"Run {run}'s receipt",
            set => string.Equals(set.Id, setId, StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false)).SourceId;
        try
        {
            var read = await AskAsync<RestoreReceiptResult>(
                client, new GetRestoreReceiptCommand(run, source, limit), cancellationToken).ConfigureAwait(false);
            WriteHead(output, read.Receipt);
            if (read.Items.Count == 0 && read.Unlisted == 0)
            {
                output.WriteLine("  every file it was asked for was restored");
            }

            foreach (var item in read.Items)
            {
                output.WriteLine(item.Detail is { } why
                    ? $"  {item.Outcome}  {item.Path} — {why}"
                    : $"  {item.Outcome}  {item.Path}");
            }

            if (read.Unlisted > 0)
            {
                output.WriteLine($"  …and {read.Unlisted} more");
            }

            return 0;
        }
        finally
        {
            await GrantedSources.CloseAsync(client, source).ConfigureAwait(false);
        }
    }

    private static void WriteHead(TextWriter output, RestoreReceiptDescriptor receipt)
    {
        if (receipt.Problem is { } problem)
        {
            output.WriteLine($"{receipt.Run}  unreadable: {problem}");
            return;
        }

        WriteHead(
            output,
            receipt.Run,
            receipt.Outcome,
            receipt.EndedAt ?? 0,
            receipt.SnapshotId,
            receipt.Set,
            Counts(receipt.Restored, receipt.FoundInPlace, receipt.ResumedWithin, receipt.Skipped, receipt.Failed, receipt.Degraded),
            receipt.WrittenTo);
    }

    private static void WriteHead(
        TextWriter output,
        string run,
        string? outcome,
        ulong endedAt,
        string? snapshotId,
        string? set,
        string counts,
        string? writtenTo)
    {
        var ended = DateTimeOffset.FromUnixTimeMilliseconds((long)endedAt).ToString("u", CultureInfo.InvariantCulture);
        var of = set is { } name ? $" of {name}" : string.Empty;
        output.WriteLine($"{run}  {outcome}  ended {ended}  snapshot {snapshotId}{of}");
        output.WriteLine($"  {counts}");
        output.WriteLine($"  written to {writtenTo}");
    }

    private static async Task<TResult> AskAsync<TResult>(
        IFallbackPlanClient client, ServiceCommand command, CancellationToken cancellationToken)
        where TResult : ServiceResult
    {
        var result = await client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ServiceError error => throw new CliFailureException(error.Message),
            TResult expected => expected,
            _ => throw new CliFailureException($"the service answered with {result.GetType().Name}."),
        };
    }

    /// <summary>
    /// A kept receipt as the service describes it (ADR-0125): what became of
    /// its run's files and where it wrote them, never the paths, which are the
    /// backup's file names. A receipt that does not read says why.
    /// </summary>
    private static RestoreReceiptDescriptor Describe(KeptRestoreReceipt kept) => kept.Receipt is not { } receipt
        ? new(kept.Run, null, null, null, null, 0, 0, 0, 0, 0, kept.Problem)
        : new(
            kept.Run,
            receipt.Outcome.ToString().ToLowerInvariant(),
            receipt.CompletedAt,
            receipt.SnapshotId,
            receipt.WrittenTo,
            Restored: receipt.Items.Count(item => item.Outcome == "restored"),
            FoundInPlace: receipt.Items.Count(item => item.FoundInPlace),
            Skipped: receipt.Items.Count(item => item.Outcome == "skipped"),
            Failed: receipt.Items.Count(item => item.Outcome == "failed"),
            Degraded: receipt.Items.Count(item => item.Outcome == "degraded"),
            Set: receipt.Set,
            SetId: receipt.SetId,
            ResumedWithin: receipt.Items.Count(item => item.ResumedAt > 0));

    /// <summary>
    /// The items' outcomes, counted. A file a resume found in place is counted
    /// restored, as its receipt counts it, and said to be.
    /// </summary>
    private static string Counts(long restored, long found, long resumed, long skipped, long failed, long degraded)
    {
        // Both are among the files restored: one found in place was left as
        // it stood, and one resumed within was written from its run's part.
        string[] among =
        [
            .. found > 0 ? [$"{found} found in place"] : Array.Empty<string>(),
            .. resumed > 0 ? [$"{resumed} resumed within"] : Array.Empty<string>(),
        ];
        var counts = among.Length > 0 ? $"{restored} restored ({string.Join(", ", among)})" : $"{restored} restored";
        counts += $", {skipped} skipped, {failed} failed";
        return degraded > 0 ? $"{counts}, {degraded} degraded" : counts;
    }
}
