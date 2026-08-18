using Bodu;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Web;

/// <summary>
/// The restore wizard's passphrase gate (ADR-0041): verified HERE, in the
/// console process on the operator's machine, against the staging archive's
/// own key files — the passphrase never crosses the command contract
/// (NFR-SEC-009 stands untouched) and never reaches the service, which
/// already holds its own copy. The same posture as key export (ADR-0028 §9):
/// passphrase work runs where the person typed it.
/// </summary>
/// <remarks>
/// This is the one class in the console permitted to reach below the client
/// contract — the dependency rule is scoped to it by name
/// (<c>DependencyRuleTests</c>), because a console that opened repositories
/// anywhere else would stop being a client. It reads the descriptor and the
/// wrapped key objects, derives, and answers; it opens no blob and derives
/// no state.
/// </remarks>
public static class ConsoleRestoreGate
{
    /// <summary>How a verification attempt resolved.</summary>
    public enum GateOutcome
    {
        /// <summary>The passphrase unwrapped a key object — it is the repository's.</summary>
        Verified = 0,

        /// <summary>The derivation ran and no key object opened.</summary>
        Wrong = 1,

        /// <summary>Nothing local to verify against — a remote console, or no archive yet.</summary>
        Unavailable = 2,
    }

    /// <summary>An attempt's answer.</summary>
    /// <param name="Outcome">How it resolved.</param>
    /// <param name="Detail">What an unavailable outcome met, for the page to show.</param>
    public sealed record GateAnswer(GateOutcome Outcome, string? Detail = null);

    /// <summary>
    /// Verifies a typed passphrase against the first staging archive under
    /// <paramref name="archivesRoot"/> that will answer. Every archive a
    /// service manages opens under the one service passphrase, so any archive
    /// is as good a witness as another; a damaged one is skipped for the
    /// next.
    /// </summary>
    /// <param name="archivesRoot">The service's archives root, from <c>describe_service</c>.</param>
    /// <param name="passphraseText">The typed passphrase; used for one derivation and released.</param>
    /// <param name="cancellationToken">Cancels the derivation.</param>
    /// <returns>The answer.</returns>
    public static async Task<GateAnswer> VerifyAsync(
        string? archivesRoot, string passphraseText, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(passphraseText);

        if (string.IsNullOrWhiteSpace(archivesRoot) || !Directory.Exists(archivesRoot))
        {
            return new GateAnswer(
                GateOutcome.Unavailable,
                "The service's archives are not readable from this console.");
        }

        using var passphrase = Passphrase.Create(passphraseText);
        var sawAnArchive = false;
        foreach (var archive in Directory.GetDirectories(archivesRoot))
        {
            if (!File.Exists(Path.Combine(archive, RepositoryLifecycle.DescriptorKey.Value)))
            {
                continue;
            }

            sawAnArchive = true;
            try
            {
                // The genuine check: derive the key-encryption key with the
                // archive's own KDF parameters and unwrap a key object. There
                // is no cheaper honest answer, and the cost is the point —
                // this is the same wall a stolen archive presents.
                _ = await RepositoryLifecycle.ExportVerifiedKeyObjectAsync(
                    new LocalFileSystemObjectStore(archive), passphrase, cancellationToken).ConfigureAwait(false);
                return new GateAnswer(GateOutcome.Verified);
            }
            catch (KeyUnwrapFailedException)
            {
                return new GateAnswer(GateOutcome.Wrong);
            }
            catch (Exception damaged) when (damaged is RepositoryOpenException or IOException)
            {
                // A damaged archive proves nothing either way; try the next.
            }
        }

        return new GateAnswer(
            GateOutcome.Unavailable,
            sawAnArchive
                ? "No local archive could answer the check."
                : "No staging archive exists yet to verify against.");
    }
}
