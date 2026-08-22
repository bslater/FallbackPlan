namespace FallbackPlan.Filesystem;

/// <summary>
/// One capture root of a publication (ADR-0040). A job with a single root
/// captures exactly the pre-multi-root shape — the tree's root is the
/// folder itself and the label is ignored. A job with several roots
/// publishes a synthetic root whose top-level entries are these roots,
/// each named by its label.
/// </summary>
/// <param name="Path">The folder to capture.</param>
/// <param name="Label">
/// The root's name inside the snapshot and the first component of every
/// rule subject under it. Required when the job has more than one root;
/// must be a plain NFC path component (no separators, no colon, not
/// <c>.</c> or <c>..</c>), unique among the job's labels both by raw
/// UTF-8 bytes and case-insensitively — it becomes a directory name at
/// restore, on any filesystem.
/// </param>
public sealed record ScanRoot(string Path, string? Label = null);
