using Bodu;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Index.Journal;

/// <summary>
/// Intent expiry (specification 08 §7): an intent expires only when
/// <b>both</b> conditions hold — the current generation exceeds
/// <c>expiry_generation</c> <em>and</em> the declared duration plus a
/// configured skew margin has elapsed. Either alone is wrong: generation
/// alone couples a slow writer's liveness to its siblings' activity, and
/// wall-clock alone reintroduces the clock dependency the design exists to
/// remove.
/// </summary>
public static class IntentLifecycle
{
    /// <summary>Whether an intent is expired under 08 §7's dual condition.</summary>
    public static bool IsExpired(
        ulong expiryGeneration,
        ulong declaredMaxDurationMs,
        ulong issuedAtMs,
        ulong currentGeneration,
        ulong nowMs,
        ulong skewMarginMs)
    {
        var generationPassed = currentGeneration > expiryGeneration;
        var durationElapsed = nowMs >= issuedAtMs + declaredMaxDurationMs + skewMarginMs;

        return generationPassed && durationElapsed;
    }
}

/// <summary>One live intent as a collector must see it (specification 08 §8).</summary>
public sealed record LiveIntent(
    WriterId WriterId,
    ulong Sequence,
    JournalPayload.WriteIntent Intent,
    ulong IssuedAt,
    IReadOnlyList<BlobId> CoveredBlobIds);

/// <summary>The collector's view of the journal: live intents plus the conservative unparseable flag.</summary>
public sealed record IntentSurvey(
    IReadOnlyList<LiveIntent> LiveIntents,
    bool HasUnparseableIntent)
{
    /// <summary>
    /// Whether <paramref name="blobId"/> is covered by any live intent —
    /// reachable, no exceptions, no heuristics (08 §8). An unparseable
    /// intent makes <em>everything</em> reachable: failing to collect wastes
    /// space; collecting wrongly loses data.
    /// </summary>
    public bool IsCovered(BlobId blobId) =>
        HasUnparseableIntent || LiveIntents.Any(intent => intent.CoveredBlobIds.Contains(blobId));
}

/// <summary>
/// Assembles the collector's intent survey from decoded journal records
/// (specification 08 §3–§8): coverage is the union of each intent's
/// <c>intended_blob_ids</c> and every extension's <c>additional_blob_ids</c>;
/// retirement is an event; expiry is 08 §7's dual condition with the
/// extension-revised duration.
/// </summary>
public static class IntentSurveyor
{
    /// <summary>Builds the survey.</summary>
    public static IntentSurvey Survey(
        IReadOnlyList<JournalRecord> records,
        int unparseableCount,
        ulong currentGeneration,
        ulong nowMs,
        ulong skewMarginMs)
    {
        ThrowHelper.ThrowIfNull(records);

        var live = new List<LiveIntent>();

        foreach (var group in records.GroupBy(record => record.WriterId))
        {
            var retired = group
                .Where(record => record.Payload is JournalPayload.IntentRetirement)
                .Select(record => ((JournalPayload.IntentRetirement)record.Payload).RetiresSequence)
                .ToHashSet();

            foreach (var record in group.Where(record => record.Payload is JournalPayload.WriteIntent))
            {
                var intent = (JournalPayload.WriteIntent)record.Payload;

                if (retired.Contains(record.Sequence))
                {
                    continue;
                }

                var extensions = group
                    .Where(candidate => candidate.Payload is JournalPayload.IntentExtension extension &&
                        extension.ExtendsSequence == record.Sequence)
                    .Select(candidate => (JournalPayload.IntentExtension)candidate.Payload)
                    .ToList();

                // An extension MAY revise the duration upward (08 §4).
                var duration = extensions.Aggregate(
                    intent.DeclaredMaxDurationMs,
                    (current, extension) => Math.Max(current, extension.DeclaredMaxDurationMs));

                if (IntentLifecycle.IsExpired(
                    intent.ExpiryGeneration, duration, record.IssuedAt, currentGeneration, nowMs, skewMarginMs))
                {
                    continue;
                }

                var covered = intent.IntendedBlobIds
                    .Concat(extensions.SelectMany(extension => extension.AdditionalBlobIds))
                    .Distinct()
                    .ToList();

                live.Add(new LiveIntent(record.WriterId, record.Sequence, intent, record.IssuedAt, covered));
            }
        }

        return new IntentSurvey(live, unparseableCount > 0);
    }
}
