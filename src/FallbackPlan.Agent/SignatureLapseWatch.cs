using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Domain;

namespace FallbackPlan.Agent;

/// <summary>
/// Names a destination's shared access signature in a notice from two weeks
/// before the expiry it states, and again once that has passed, until a
/// credential that does not lapse so soon is stored (FR-DEST-005; ADR-0093
/// Amendment 1).
/// </summary>
/// <remarks>
/// <para>
/// The expiry is in the token the service holds, so learning it sends
/// nothing and loads nothing, and the background window, which holds back
/// work on the machine and the link, does not hold it back. A destination
/// synced weekly, or kept only to restore from, fails nothing when its
/// signature lapses, which is why the lapse is a notice and not only a
/// failed sync.
/// </para>
/// <para>
/// A notice a person acknowledged is not raised again while it would say the
/// same thing. The lapse says something new, and so does a signature with
/// another expiry.
/// </para>
/// </remarks>
internal static class SignatureLapseWatch
{
    /// <summary>How long before its lapse a signature is named.</summary>
    internal static readonly TimeSpan LeadTime = TimeSpan.FromDays(14);

    private const string NoticeKind = "signature-lapsing";

    /// <summary>
    /// Raises the notice for every declared destination whose signature lapses
    /// within <see cref="LeadTime"/> or has lapsed, and resolves every other.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="destinations">Every destination the configuration declares.</param>
    /// <param name="now">The clock.</param>
    public static void Observe(
        ServiceRuntime runtime, IEnumerable<DestinationConfiguration> destinations, DateTimeOffset now)
    {
        var credentials = runtime.DestinationCredentials;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var standing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in destinations)
        {
            if (destination.Kind != DestinationKind.AzureBlob
                || credentials.KindHeld(destination.Id) is { } kind
                    && kind != DestinationCredentialStore.SharedAccessSignatureKind)
            {
                continue;
            }

            var key = $"{NoticeKind}:{destination.Id}";
            DateTimeOffset? expires;
            try
            {
                expires = credentials.TryLoadAzureBlob(destination.Id)?.Expires;
            }
            catch (Exception exception)
                when (exception is ClientStateException or IOException or UnauthorizedAccessException)
            {
                // Damage is said where the credential is used. It is not a
                // renewal, so what was said stands.
                standing.Add(key);
                continue;
            }

            if (expires is not { } lapses || lapses - now > LeadTime)
            {
                continue;
            }

            standing.Add(key);
            runtime.Notices.RaiseUnlessAcknowledged(key, MessageFor(destination.Name, lapses, now), nowMs);
        }

        foreach (var notice in runtime.Notices.Unacknowledged)
        {
            if (notice.Kind == NoticeKind && !standing.Contains(notice.Key))
            {
                runtime.Notices.Resolve(notice.Key, nowMs);
            }
        }
    }

    /// <summary>
    /// What the notice says: one text while the signature is honoured and
    /// another once it is not, each the same on every pass, so that an
    /// acknowledgement holds until the lapse.
    /// </summary>
    private static string MessageFor(string destinationName, DateTimeOffset lapses, DateTimeOffset now) =>
        lapses <= now
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The shared access signature stored for destination '{destinationName}' expired at "
                + $"{lapses.UtcDateTime:yyyy-MM-dd HH:mm} UTC, so nothing is sent under it. Store a new one in "
                + $"the console's destination editor, or with `fallbackplan destination-credentials "
                + $"{destinationName} --sas-env`.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The shared access signature stored for destination '{destinationName}' expires at "
                + $"{lapses.UtcDateTime:yyyy-MM-dd HH:mm} UTC, and nothing is sent under it after then. Store a "
                + $"new one before then, in the console's destination editor or with `fallbackplan "
                + $"destination-credentials {destinationName} --sas-env`.");
}
