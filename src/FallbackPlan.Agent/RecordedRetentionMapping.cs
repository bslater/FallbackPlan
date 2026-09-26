using FallbackPlan.Application;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Agent;

/// <summary>
/// A set's own retention between the configuration and the policy manifest
/// that records it (FR-DR-006): written with every publication, read back when
/// an archive's set is re-declared (ADR-0061 Amendment 1).
/// </summary>
internal static class RecordedRetentionMapping
{
    /// <summary>
    /// What the policy manifest records of <paramref name="retention"/>; null
    /// when the set defers retention, which records nothing.
    /// </summary>
    /// <param name="retention">The set's own policy, never a destination's override (FR-DEST-006).</param>
    internal static RecordedRetention? ToRecorded(RetentionConfiguration? retention) =>
        retention is null
            ? null
            : new RecordedRetention
            {
                KeepDaily = Recorded(retention.KeepDaily),
                KeepWeekly = Recorded(retention.KeepWeekly),
                KeepMonthly = Recorded(retention.KeepMonthly),
                MinGenerations = Recorded(retention.MinGenerations),
                DeferralDays = Recorded(retention.DeferralDays),
            };

    /// <summary>
    /// The configuration a recorded retention re-declares; null when the
    /// archive recorded none.
    /// </summary>
    /// <remarks>
    /// A rule past what a configuration can hold is read as the largest one it
    /// can: two billion days and four billion both mean "never", and the
    /// policy should survive the difference. A zero is passed through for the
    /// caller to refuse, because the configuration's own check is what says a
    /// zero rule is a typo rather than a policy.
    /// </remarks>
    /// <param name="recorded">What the archive's newest policy manifest recorded.</param>
    internal static RetentionConfiguration? FromRecorded(RecordedRetention? recorded) =>
        recorded is null
            ? null
            : new RetentionConfiguration
            {
                KeepDaily = Configured(recorded.KeepDaily),
                KeepWeekly = Configured(recorded.KeepWeekly),
                KeepMonthly = Configured(recorded.KeepMonthly),
                MinGenerations = Configured(recorded.MinGenerations),
                DeferralDays = Configured(recorded.DeferralDays),
            };

    // A configured rule is validated positive before it can reach a backup, so
    // a negative one here is a state nothing can produce, and it throws rather
    // than wrapping into four billion.
    private static uint? Recorded(int? value) => value is { } rule ? checked((uint)rule) : null;

    private static int? Configured(uint? value) => value is { } rule ? (int)Math.Min(rule, int.MaxValue) : null;
}
