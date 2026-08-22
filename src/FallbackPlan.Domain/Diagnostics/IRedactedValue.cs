namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// A value that renders differently once it leaves the machine that wrote it
/// (ADR-0043 §4; NFR-SEC-006, NFR-PRIV-003).
/// </summary>
/// <remarks>
/// <para>
/// Redaction here is <b>by declared type</b>, never by string pattern: a field
/// is classified where it is declared, so a newly added path-bearing or
/// correlatable field is protected by construction rather than by somebody
/// remembering to extend a filter list. String matching fails silently, and it
/// fails exactly when it matters (specification 03 §8).
/// </para>
/// <para>
/// This interface is for values that are <em>useful locally and unsafe
/// remotely</em> — a path, a repository identifier. Values that are unsafe
/// everywhere do not implement it: a secret redacts in its own
/// <see cref="object.ToString"/> at the declaration site, so it is already safe
/// wherever it appears. <c>Passphrase</c>, <c>Kek</c>,
/// <c>RepositoryWriteCredential</c>, <c>RepositoryReadAuthority</c>,
/// <c>KeyBundle</c> and <see cref="Identifiers.ContentId"/> work that way and
/// are deliberately untouched by this.
/// </para>
/// <para>
/// The boundary a record crosses decides which rendering applies: the
/// service's own log file is inside the trust boundary and records
/// <see cref="object.ToString"/>; anything travelling to a client or into an
/// exported bundle records <see cref="ToRedactedString"/>
/// (architecture 10 §4, §6).
/// </para>
/// </remarks>
public interface IRedactedValue
{
    /// <summary>
    /// The rendering safe to send off the machine: correlatable with other
    /// records of the same value, and revealing nothing about its content.
    /// </summary>
    /// <remarks>
    /// Implementations must be <b>stable</b> for a given value within a
    /// process — "the same file failed twice" and "two different files failed"
    /// are different bugs, and an operator reading a redacted feed still has to
    /// be able to tell them apart.
    /// </remarks>
    string ToRedactedString();
}
