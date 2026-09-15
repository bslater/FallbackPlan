using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The bytes of an object that arrived before the link died, kept so the next
/// session can begin where the last one stopped
/// ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
/// </summary>
/// <remarks>
/// <para>
/// A staged prefix is <b>scratch, not state</b>, and everything here follows
/// from that. It lives outside the replica store — under
/// <c>&lt;state&gt;/spool/replication/&lt;repository&gt;/</c>, which no object
/// key can spell — so it answers no read, appears in no listing, and is
/// invisible to a possession challenge. Losing one costs a re-send and nothing
/// else, which is why every doubt here resolves by deleting it.
/// </para>
/// <para>
/// Each prefix is a pair: <c>&lt;hash&gt;.partial</c> holding the bytes, and
/// <c>&lt;hash&gt;.meta</c> naming the object and the length it is being
/// staged towards. The hash is of the key because a store key may be a
/// kilobyte long and contain slashes; the sidecar is what makes the file
/// nameable again, and an unpaired file of either kind is swept — the shape
/// <c>Repository.Packing/SpoolCheckpoint</c> uses for the writer's own spool,
/// for the same reason.
/// </para>
/// </remarks>
internal static class PartialSpool
{
    /// <summary>How long a staged prefix waits for the session that resumes it.</summary>
    /// <remarks>
    /// Long enough to cover the back-off of a destination that is simply
    /// unplugged for the weekend, short enough that abandoned bytes are not a
    /// permanent charge against the peer's quota. A swept prefix costs a
    /// re-send, so the failure is ordinary rather than serious.
    /// </remarks>
    public const int RetainedDays = 7;

    private const string PartialExtension = ".partial";
    private const string SidecarExtension = ".meta";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One staged prefix, as the destination declares it.</summary>
    /// <param name="Key">The object it belongs to.</param>
    /// <param name="Staged">How many bytes are staged.</param>
    /// <param name="Digest">SHA-256 of exactly those bytes.</param>
    public sealed record Declaration(string Key, ulong Staged, byte[] Digest);

    /// <summary>The directory holding one repository's staged prefixes.</summary>
    /// <param name="spoolRoot">The listener's replication spool root.</param>
    /// <param name="repositoryIdHex">The repository the bytes belong to.</param>
    /// <returns>The directory path, which may not exist.</returns>
    public static string DirectoryFor(string spoolRoot, string repositoryIdHex) =>
        Path.Combine(spoolRoot, repositoryIdHex);

    /// <summary>The staged-prefix file for one object.</summary>
    /// <param name="directory">This repository's spool directory.</param>
    /// <param name="key">The object key.</param>
    /// <returns>The file path, which may not exist.</returns>
    public static string PathFor(string directory, string key) =>
        Path.Combine(directory, Name(key) + PartialExtension);

    /// <summary>
    /// What this repository holds part of: swept of the stale and the
    /// superseded, then hashed.
    /// </summary>
    /// <remarks>
    /// The digest is computed here, from the file, rather than remembered from
    /// when the bytes arrived. That is the whole of the check: a prefix that
    /// rotted on disk between sessions produces a digest the source will not
    /// match, and the object restarts instead of being resumed on top of
    /// damage. It costs one read of the staged bytes, which is the cost of not
    /// re-sending them.
    /// </remarks>
    /// <param name="directory">This repository's spool directory.</param>
    /// <param name="replica">The replica store, so a prefix of a committed object can be dropped.</param>
    /// <param name="now">The clock the retention window is measured from.</param>
    /// <param name="limit">How many to declare at most.</param>
    /// <param name="cancellationToken">Stops the survey.</param>
    /// <returns>What to declare, oldest last.</returns>
    public static async ValueTask<IReadOnlyList<Declaration>> SurveyAsync(
        string directory,
        LocalFileSystemObjectStore replica,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(directory);
        ThrowHelper.ThrowIfNull(replica);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var declarations = new List<Declaration>();
        foreach (var path in Directory.GetFiles(directory, "*" + PartialExtension)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sidecar = Path.ChangeExtension(path, SidecarExtension);
            if (ReadSidecar(sidecar) is not { } pinned)
            {
                // No sidecar, or one that will not parse: the bytes cannot be
                // named, so they cannot be offered and are not worth keeping.
                Discard(path);
                continue;
            }

            var staged = new FileInfo(path);
            if (!staged.Exists
                || staged.LastWriteTimeUtc < now.UtcDateTime.AddDays(-RetainedDays)
                || staged.Length == 0
                || (ulong)staged.Length >= pinned.Length)
            {
                // Stale, empty, or as long as the object it stages towards —
                // the last meaning a session died between the final chunk and
                // the commit, where a re-send is the honest answer.
                Discard(path);
                continue;
            }

            if (ObjectKey.TryParse(pinned.Key, out var objectKey)
                && (await replica.GetMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false)).Found)
            {
                // The object arrived by some other route since. The prefix is
                // now a duplicate of bytes already committed.
                Discard(path);
                continue;
            }

            if (declarations.Count >= limit)
            {
                continue;
            }

            if (await DigestAsync(path, cancellationToken).ConfigureAwait(false) is { } digest)
            {
                declarations.Add(new Declaration(pinned.Key, (ulong)staged.Length, digest));
            }
        }

        return declarations;
    }

    /// <summary>Opens or creates the staged file for an object, at the offset the source named.</summary>
    /// <remarks>
    /// Truncation is unconditional and the source's number is what it
    /// truncates to, including zero. The destination's declaration is a claim;
    /// the source is the only side that can check it against the object's real
    /// bytes, so the source's answer decides and this side does as it is told.
    /// </remarks>
    /// <param name="directory">This repository's spool directory.</param>
    /// <param name="key">The object key.</param>
    /// <param name="length">The object's declared total length.</param>
    /// <param name="resumeOffset">Where the source will begin.</param>
    /// <returns>The open file, positioned to append.</returns>
    /// <exception cref="PeerProtocolException">The staged file is shorter than the offset claimed.</exception>
    public static FileStream Open(string directory, string key, ulong length, ulong resumeOffset)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(directory, key);

        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if ((ulong)file.Length < resumeOffset)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A transfer resumes at {resumeOffset}; only {file.Length} bytes are staged.");
            }

            file.SetLength((long)resumeOffset);
            file.Seek(0, SeekOrigin.End);
            WriteSidecar(Path.ChangeExtension(path, SidecarExtension), key, length);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Removes a staged prefix and its sidecar.</summary>
    /// <param name="path">The staged file.</param>
    public static void Discard(string path)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        Delete(path);
        Delete(Path.ChangeExtension(path, SidecarExtension));
    }

    /// <summary>
    /// Bytes staged across every repository, for the quota the peer's
    /// committed bytes are counted against (05 §1).
    /// </summary>
    /// <remarks>
    /// Counted because they are real disk this peer is costing its host. The
    /// alternative — leaving them out — lets a peer park bytes outside the
    /// ceiling it agreed to by starting transfers it never finishes.
    /// </remarks>
    /// <param name="spoolRoot">The listener's replication spool root.</param>
    /// <param name="repositoryIdsHex">The repositories owned by the peer in question.</param>
    /// <returns>The total staged.</returns>
    public static ulong StagedBytes(string spoolRoot, IEnumerable<string> repositoryIdsHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);
        ThrowHelper.ThrowIfNull(repositoryIdsHex);

        var total = 0UL;
        foreach (var repositoryIdHex in repositoryIdsHex)
        {
            var directory = DirectoryFor(spoolRoot, repositoryIdHex);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.GetFiles(directory, "*" + PartialExtension))
            {
                try
                {
                    total += (ulong)new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // A file that vanished between listing and measuring costs
                    // nothing, which is the honest answer to add.
                }
            }
        }

        return total;
    }

    /// <summary>Deletes prefixes nothing can resume — unpaired files, and anything past its window.</summary>
    /// <param name="spoolRoot">The listener's replication spool root.</param>
    /// <param name="now">The clock.</param>
    public static void Sweep(string spoolRoot, DateTimeOffset now)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);

        if (!Directory.Exists(spoolRoot))
        {
            return;
        }

        var cutoff = now.UtcDateTime.AddDays(-RetainedDays);
        foreach (var directory in Directory.GetDirectories(spoolRoot))
        {
            foreach (var path in Directory.GetFiles(directory))
            {
                var isPartial = path.EndsWith(PartialExtension, StringComparison.Ordinal);
                var isSidecar = path.EndsWith(SidecarExtension, StringComparison.Ordinal);
                var pair = isPartial
                    ? Path.ChangeExtension(path, SidecarExtension)
                    : Path.ChangeExtension(path, PartialExtension);

                if ((!isPartial && !isSidecar)
                    || !File.Exists(pair)
                    || File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    Delete(path);
                }
            }
        }
    }

    /// <summary>
    /// The staged file's name: a digest of the object key, because a key may
    /// be a kilobyte long and carry separators no file name may.
    /// </summary>
    /// <param name="key">The object key.</param>
    private static string Name(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private static async ValueTask<byte[]?> DigestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            return await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Held by another session, or gone. Either way it is not ours to
            // offer: a partial two sessions were resuming at once is a race
            // neither would win.
            return null;
        }
    }

    private static void WriteSidecar(string path, string key, ulong length)
    {
        var pinned = new Sidecar { Key = key, Length = length };
        File.WriteAllText(path, JsonSerializer.Serialize(pinned, SerializerOptions), Encoding.UTF8);
    }

    private static Sidecar? ReadSidecar(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var pinned = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), SerializerOptions);
            return pinned is null || string.IsNullOrEmpty(pinned.Key) || pinned.Length == 0 ? null : pinned;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Scratch that will not delete is swept on the next pass.
        }
    }

    /// <summary>What a staged file cannot say about itself: which object it belongs to.</summary>
    private sealed class Sidecar
    {
        /// <summary>The object key.</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>The length the prefix is being staged towards.</summary>
        [JsonPropertyName("length")]
        public ulong Length { get; set; }
    }
}
