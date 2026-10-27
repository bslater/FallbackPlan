using System.Globalization;
using FallbackPlan.Cli.Resources;
using FallbackPlan.Restore;

namespace FallbackPlan.Cli;

/// <summary>
/// What <c>restore-receipts</c> prints (FR-RST-004, ADR-0124): the restore
/// receipts a state directory keeps, by the service or in direct mode alike,
/// each run newest first with what it did and where it wrote, or one run's
/// receipt whole as it was kept.
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

            var ended = DateTimeOffset.FromUnixTimeMilliseconds((long)receipt.CompletedAt)
                .ToString("u", CultureInfo.InvariantCulture);
            var of = receipt.Set is { } set ? $" of {set}" : string.Empty;
            output.WriteLine($"{entry.Run}  {receipt.Outcome}  ended {ended}  snapshot {receipt.SnapshotId}{of}");
            output.WriteLine($"  {Counts(receipt.Items)}");
            output.WriteLine($"  written to {receipt.WrittenTo}");
        }

        output.WriteLine(shown.Count == kept.Count
            ? $"{kept.Count} restore {(kept.Count == 1 ? "receipt" : "receipts")} kept under '{directory}'."
            : $"The newest {shown.Count} of {kept.Count} restore receipts kept under '{directory}'.");
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
        if (!RestoreReceipt.IsRunName(run))
        {
            throw new CliFailureException(Strings.RestoreReceiptReport_NotARunsName);
        }

        var directory = Path.Combine(stateDirectory, "receipts");
        var path = Path.Combine(directory, $"{run}.json");
        if (!File.Exists(path))
        {
            throw new CliFailureException(Strings.FormatRestoreReceiptReport_NoReceiptKept(run, directory));
        }

        output.WriteLine(File.ReadAllText(path).TrimEnd());
    }

    /// <summary>
    /// The items' outcomes, counted. A file a resume found in place is counted
    /// restored, as its receipt counts it, and said to be.
    /// </summary>
    private static string Counts(IReadOnlyList<ReceiptItem> items)
    {
        var restored = items.Count(item => item.Outcome == "restored");
        var found = items.Count(item => item.FoundInPlace);
        var skipped = items.Count(item => item.Outcome == "skipped");
        var failed = items.Count(item => item.Outcome == "failed");
        var degraded = items.Count(item => item.Outcome == "degraded");

        var counts = found > 0 ? $"{restored} restored ({found} found in place)" : $"{restored} restored";
        counts += $", {skipped} skipped, {failed} failed";
        return degraded > 0 ? $"{counts}, {degraded} degraded" : counts;
    }
}
