namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// The one place a correlatable identifier is shortened for the wire
/// (ADR-0043 §4).
/// </summary>
/// <remarks>
/// Identifiers already render as hex or base32, so redacting one is a prefix
/// rather than a second digest — the rendering is already opaque, and what
/// NFR-PRIV-002 objects to is its use as a durable handle that follows a user
/// between stores. A prefix keeps records correlatable within one feed and
/// stops being a repository-wide identity.
/// </remarks>
public static class Redaction
{
    /// <summary>
    /// Renders <paramref name="rendering"/> as <c>prefix#head</c>, taking at
    /// most <paramref name="characters"/> characters of it.
    /// </summary>
    /// <param name="prefix">The short kind name, e.g. <c>repo</c>.</param>
    /// <param name="rendering">The full rendering to shorten.</param>
    /// <param name="characters">How many characters of it to keep.</param>
    /// <remarks>
    /// The length is clamped rather than trusted: this runs inside logging, and
    /// a diagnostic that throws while describing a failure replaces a
    /// diagnosable problem with an undiagnosable one.
    /// </remarks>
    public static string Shorten(string prefix, string rendering, int characters) =>
        string.Concat(prefix, "#", rendering.AsSpan(0, Math.Min(characters, rendering.Length)));
}
