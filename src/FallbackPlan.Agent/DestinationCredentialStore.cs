using System.Text;
using System.Text.Json;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Storage.S3;

namespace FallbackPlan.Agent;

/// <summary>
/// The access keys this service signs S3-compatible destinations' requests
/// with (ADR-0091): one file per destination under
/// <c>&lt;state&gt;/destination-credentials/</c>, by the destination's id,
/// owner-only and replaced atomically (NFR-SEC-012). Never in the
/// configuration file, which stays exportable as it is (NFR-OPS-003), and
/// never in the repository or any replica.
/// </summary>
/// <remarks>
/// By id rather than name: a rename keeps its key, and a destination deleted
/// and declared again under the same name is another destination that has
/// its own key stored. The id is checked before it becomes part of a path,
/// so no declaration can name a file outside this directory.
/// </remarks>
public sealed class DestinationCredentialStore(string stateDirectory)
{
    private string Root => Path.Combine(stateDirectory, "destination-credentials");

    private string PathFor(string destinationId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationId);
        if (destinationId.Length != 32 || !destinationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                $"A destination id is 32 hex characters; '{destinationId}' is not one.", nameof(destinationId));
        }

        return Path.Combine(Root, $"{destinationId.ToLowerInvariant()}.json");
    }

    /// <summary>Whether an access key is held for the destination.</summary>
    /// <param name="destinationId">The destination's id.</param>
    public bool Holds(string destinationId) => File.Exists(PathFor(destinationId));

    /// <summary>The destination's access key, or null when none is held.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <exception cref="ClientStateException">A key file is held and does not read as one.</exception>
    public S3Credentials? TryLoad(string destinationId)
    {
        var path = PathFor(destinationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            return new S3Credentials(
                document.RootElement.GetProperty("access_key_id").GetString()!,
                document.RootElement.GetProperty("secret_access_key").GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException)
        {
            // Damage to name, as a damaged write credential is: reading it as
            // "no key stored" would send the person to store one they already
            // stored, and never say the file went bad.
            throw new ClientStateException(
                $"The access key stored for destination {destinationId} at '{path}' is damaged — store it again "
                + "(ADR-0091).");
        }
    }

    /// <summary>Holds the destination's access key, replacing any held one.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <param name="credentials">The key.</param>
    public void Save(string destinationId, S3Credentials credentials)
    {
        ThrowHelper.ThrowIfNull(credentials);
        var path = PathFor(destinationId);
        Directory.CreateDirectory(Root);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("access_key_id", credentials.AccessKeyId);
            writer.WriteString("secret_access_key", credentials.SecretAccessKey);
            writer.WriteEndObject();
        }

        // Owner-only before the key is in it, then renamed into place: there
        // is no moment at which the file holds the key and another local
        // account may read it, nor one at which a crash leaves half a key.
        var temporary = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                stream.Write(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        finally
        {
            Array.Clear(buffer.GetBuffer());
        }
    }

    /// <summary>Forgets the destination's access key, if one is held.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <returns>Whether one was held.</returns>
    public bool Delete(string destinationId)
    {
        var path = PathFor(destinationId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
