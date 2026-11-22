using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bodu;
using FallbackPlan.Domain;

namespace FallbackPlan.Agent;

/// <summary>
/// The blobs a backup needed at a destination, kept when its proof there
/// ended ([ADR-0199](../../docs/adr/0199-a-backup-is-proven-at-each-destination-it-lands-at.md)
/// Amendment 5): one file per set and destination under
/// <c>&lt;state&gt;/proven-blobs/</c>, replaced atomically when a newer
/// backup's proof ends. What the next backup's proof subtracts when the
/// catalogue no longer holds the backup they were proven through: a person
/// deleted it, or the catalogue was rebuilt without it. Retention's expiry
/// keeps a backup's row, so the catalogue still answers for that one.
/// </summary>
/// <remarks>
/// Names only, the last segment of each blob's store key, as the proof names
/// them. A file that does not read, or was kept for another backup than the
/// one the ledger names proven, says nothing: the proof then asks about every
/// blob, as it did before any was kept.
/// </remarks>
internal sealed class ProvenBlobsStore(string stateDirectory)
{
    private string Root => Path.Combine(stateDirectory, "proven-blobs");

    /// <summary>
    /// The names kept for <paramref name="backup"/> at the destination, or null
    /// where none were kept for it, or none read.
    /// </summary>
    /// <param name="setId">The set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="backup">The record key of the backup the destination was proven through.</param>
    /// <returns>The names, or null.</returns>
    public IReadOnlySet<string>? Read(string setId, string destination, string backup)
    {
        ThrowHelper.ThrowIfNull(backup);
        var path = PathFor(setId, destination);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("backup").GetString(), backup, StringComparison.Ordinal))
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in root.GetProperty("names").EnumerateArray())
            {
                names.Add(name.GetString() ?? throw new FormatException("a kept name is null"));
            }

            return names;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the names of the blobs <paramref name="backup"/> needed at the
    /// destination, in place of any kept for an earlier backup.
    /// </summary>
    /// <param name="setId">The set.</param>
    /// <param name="destination">The destination's declared name.</param>
    /// <param name="backup">The record key of the backup now proven there.</param>
    /// <param name="names">The names of the blobs it needed.</param>
    public void Keep(string setId, string destination, string backup, IEnumerable<string> names)
    {
        ThrowHelper.ThrowIfNull(backup);
        ThrowHelper.ThrowIfNull(names);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("backup", backup);
            writer.WriteStartArray("names");
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(
            PathFor(setId, destination), Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    /// <summary>
    /// The set's folder, and the destination's name hashed, so any name a
    /// configuration allows is a file name every platform takes.
    /// </summary>
    private string PathFor(string setId, string destination)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        ThrowHelper.ThrowIfNullOrWhiteSpace(destination);
        if (setId.Length != 32 || !setId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"A set id is 32 hex characters; '{setId}' is not one.", nameof(setId));
        }

        var file = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..32];
        return Path.Combine(Root, setId.ToLowerInvariant(), $"{file}.json");
    }
}
