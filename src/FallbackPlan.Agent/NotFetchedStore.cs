using System.Text;
using System.Text.Json;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Domain;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

/// <summary>
/// What each set's last backup could not fetch of its online-only files
/// (ADR-0193): one file per set under <c>&lt;state&gt;/not-fetched/</c>, by
/// the set's id, replaced atomically, and none where that backup fetched every
/// one. A file that does not read says nothing, which costs the set's status
/// the word until its next backup.
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
            return new NotFetchedDescriptor(
                root.GetProperty("files").GetInt64(),
                root.GetProperty("keptEarlier").GetInt64(),
                root.GetProperty("since").GetUInt64(),
                root.GetProperty("path").GetString() ?? throw new FormatException("no path"),
                root.GetProperty("reason").GetString() ?? throw new FormatException("no reason"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps what a backup at <paramref name="nowMs"/> did not fetch, since
    /// the first of the backups before it that each missed some; or lets go
    /// of what an earlier one kept, where this one fetched every one.
    /// </summary>
    public void Keep(string setId, NotFetchedFiles? notFetched, ulong nowMs)
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
