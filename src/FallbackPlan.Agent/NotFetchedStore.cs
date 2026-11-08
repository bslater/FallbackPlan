using System.Text;
using System.Text.Json;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Domain;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

/// <summary>
/// What each set's last backup could not fetch of its online-only files
/// (ADR-0193), and could not list of the folders their provider holds
/// (Amendment 3): one file per set under <c>&lt;state&gt;/not-fetched/</c>,
/// by the set's id, replaced atomically, and none where that backup missed
/// none. It names that backup's snapshot and whether it was behind (ADR-0193
/// Amendment 1). A file that does not read says nothing, which costs the set's
/// status the word until its next backup.
/// </summary>
internal sealed class NotFetchedStore(string stateDirectory)
{
    private string Root => Path.Combine(stateDirectory, "not-fetched");

    /// <summary>What the set's last backup did not fetch, or null where it fetched every one or nothing reads.</summary>
    public NotFetchedDescriptor? Read(string setId)
    {
        var path = PathFor(setId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            var root = document.RootElement;
            // A record kept before folders were counted says nothing of them
            // (ADR-0193 Amendment 3), nor does one of files alone.
            return new NotFetchedDescriptor(
                root.GetProperty("files").GetInt64(),
                root.GetProperty("keptEarlier").GetInt64(),
                root.GetProperty("since").GetUInt64(),
                root.GetProperty("path").GetString() ?? throw new FormatException("no path"),
                root.GetProperty("reason").GetString() ?? throw new FormatException("no reason"),
                root.TryGetProperty("folders", out var folders) ? folders.GetInt64() : null,
                root.TryGetProperty("foldersKeptEarlier", out var foldersKept) ? foldersKept.GetInt64() : null,
                root.TryGetProperty("listing", out var listing) ? listing.GetBoolean() : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the set's last backup, the one whose snapshot is
    /// <paramref name="snapshotId"/>, was behind (ADR-0193 Amendment 1): its
    /// every failure an online-only file that kept its version before. Never
    /// for another snapshot, as another writer's, nor where the record does
    /// not say, as one kept before it did.
    /// </summary>
    public bool IsBehind(string setId, ReadOnlySpan<byte> snapshotId)
    {
        var path = PathFor(setId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            var root = document.RootElement;
            return root.TryGetProperty("behind", out var behind) && behind.GetBoolean()
                && root.TryGetProperty("snapshot", out var snapshot)
                && string.Equals(snapshot.GetString(), Convert.ToHexStringLower(snapshotId), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps what a backup at <paramref name="nowMs"/> did not fetch, since
    /// the first of the backups before it that each missed some; or lets go
    /// of what an earlier one kept, where this one fetched every one. Its
    /// snapshot is <paramref name="snapshotId"/>, and whether it was behind
    /// is kept with it.
    /// </summary>
    public void Keep(string setId, NotFetchedFiles? notFetched, ulong nowMs, ReadOnlySpan<byte> snapshotId, bool isBehind)
    {
        var path = PathFor(setId);
        if (notFetched is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var since = Read(setId)?.Since ?? nowMs;
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("files", notFetched.Files);
            writer.WriteNumber("keptEarlier", notFetched.KeptEarlier);
            writer.WriteNumber("since", since);
            writer.WriteString("path", notFetched.Path);
            writer.WriteString("reason", notFetched.Reason);
            if (notFetched.Folders > 0)
            {
                writer.WriteNumber("folders", notFetched.Folders);
                writer.WriteNumber("foldersKeptEarlier", notFetched.FoldersKept);
            }

            if (notFetched.Listing)
            {
                writer.WriteBoolean("listing", true);
            }

            writer.WriteString("snapshot", Convert.ToHexStringLower(snapshotId));
            writer.WriteBoolean("behind", isBehind);
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private string PathFor(string setId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        if (setId.Length != 32 || !setId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"A set id is 32 hex characters; '{setId}' is not one.", nameof(setId));
        }

        return Path.Combine(Root, $"{setId.ToLowerInvariant()}.json");
    }
}
