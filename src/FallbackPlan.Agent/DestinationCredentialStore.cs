using System.Text.Json;
using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Storage.AzureBlob;
using FallbackPlan.Storage.S3;

namespace FallbackPlan.Agent;

/// <summary>
/// The credentials this service authorises object-store destinations'
/// requests with (ADR-0091, ADR-0093): an S3-compatible store's access key,
/// or an Azure Blob container's account key or shared access signature. One
/// file per destination under <c>&lt;state&gt;/destination-credentials/</c>,
/// by the destination's id, owner-only and replaced atomically
/// (NFR-SEC-012). Never in the configuration file, which stays exportable as
/// it is (NFR-OPS-003), and never in the repository or any replica.
/// </summary>
/// <remarks>
/// <para>
/// By id rather than name: a rename keeps its credential, and a destination
/// deleted and declared again under the same name is another destination
/// that has its own stored. The id is checked before it becomes part of a
/// path, so no declaration can name a file outside this directory.
/// </para>
/// <para>
/// A file says which credential it holds: an access key in the shape it has
/// always had, and the two Azure Blob credentials under a <c>kind</c>. Each
/// destination kind reads only the credentials it can use, so one whose kind
/// was changed after a credential was stored is told it holds none, rather
/// than handed another API's secret.
/// </para>
/// </remarks>
public sealed class DestinationCredentialStore(string stateDirectory)
{
    /// <summary>An S3-compatible store's access key, in the contract's spelling.</summary>
    public const string AccessKeyKind = "access-key";

    /// <summary>An Azure Blob storage account's key, in the contract's spelling.</summary>
    public const string SharedKeyKind = "shared-key";

    /// <summary>A shared access signature for an Azure Blob container, in the contract's spelling.</summary>
    public const string SharedAccessSignatureKind = "sas";

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

    /// <summary>Whether any credential is held for the destination.</summary>
    /// <param name="destinationId">The destination's id.</param>
    public bool Holds(string destinationId) => File.Exists(PathFor(destinationId));

    /// <summary>
    /// Whether a credential the destination's kind can use is held for it. A
    /// file that does not read as any credential counts as held, so that it is
    /// called damaged where it is used rather than said never to have been
    /// stored; one of a kind another API uses does not.
    /// </summary>
    /// <param name="destination">The destination.</param>
    public bool HoldsFor(DestinationConfiguration destination)
    {
        ThrowHelper.ThrowIfNull(destination);
        return Holds(destination.Id) && (KindHeld(destination.Id) is not { } kind || Fits(kind, destination.Kind));
    }

    /// <summary>
    /// Which credential is held for the destination, in the contract's
    /// spelling, or null when none is, or the file does not read as one.
    /// </summary>
    /// <param name="destinationId">The destination's id.</param>
    public string? KindHeld(string destinationId)
    {
        var path = PathFor(destinationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            return document.RootElement.TryGetProperty("kind", out var kind) ? kind.GetString() : AccessKeyKind;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Whether a credential of <paramref name="credentialKind"/> is one a destination of <paramref name="kind"/> uses.</summary>
    /// <param name="credentialKind">The credential's kind, in the contract's spelling.</param>
    /// <param name="kind">The destination's kind.</param>
    public static bool Fits(string credentialKind, DestinationKind kind) => (credentialKind, kind) switch
    {
        (AccessKeyKind, DestinationKind.S3) => true,
        (SharedKeyKind or SharedAccessSignatureKind, DestinationKind.AzureBlob) => true,
        _ => false,
    };

    /// <summary>The destination's access key, or null when none is held.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <exception cref="ClientStateException">A credential file is held and does not read as one.</exception>
    public S3Credentials? TryLoad(string destinationId) =>
        Read(destinationId, root => root.TryGetProperty("kind", out _)
            ? null
            : new S3Credentials(
                root.GetProperty("access_key_id").GetString()!,
                root.GetProperty("secret_access_key").GetString()!));

    /// <summary>The destination's account key or shared access signature, or null when neither is held.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <exception cref="ClientStateException">A credential file is held and does not read as one.</exception>
    public AzureBlobCredentials? TryLoadAzureBlob(string destinationId) =>
        Read(destinationId, root => (root.TryGetProperty("kind", out var kind) ? kind.GetString() : null) switch
        {
            SharedKeyKind => AzureBlobCredentials.SharedKey(root.GetProperty("account_key").GetString()!),
            SharedAccessSignatureKind => AzureBlobCredentials.SharedAccessSignature(root.GetProperty("sas_token").GetString()!),
            _ => null,
        });

    /// <summary>Holds the destination's access key, replacing any held credential.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <param name="credentials">The key.</param>
    public void Save(string destinationId, S3Credentials credentials)
    {
        ThrowHelper.ThrowIfNull(credentials);
        Write(destinationId, writer =>
        {
            writer.WriteString("access_key_id", credentials.AccessKeyId);
            writer.WriteString("secret_access_key", credentials.SecretAccessKey);
        });
    }

    /// <summary>Holds the destination's account key or shared access signature, replacing any held credential.</summary>
    /// <param name="destinationId">The destination's id.</param>
    /// <param name="credentials">The credential.</param>
    public void Save(string destinationId, AzureBlobCredentials credentials)
    {
        ThrowHelper.ThrowIfNull(credentials);
        Write(destinationId, writer =>
        {
            if (credentials.Kind == AzureBlobCredentialKind.SharedKey)
            {
                writer.WriteString("kind", SharedKeyKind);
                writer.WriteString("account_key", credentials.Secret);
            }
            else
            {
                writer.WriteString("kind", SharedAccessSignatureKind);
                writer.WriteString("sas_token", credentials.Secret);
            }
        });
    }

    /// <summary>Forgets the destination's credential, if one is held.</summary>
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

    private T? Read<T>(string destinationId, Func<JsonElement, T?> read)
        where T : class
    {
        var path = PathFor(destinationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllText(path));
            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException)
        {
            // Damage to name, as a damaged write credential is: reading it as
            // "none stored" would send the person to store one they already
            // stored, and never say the file went bad.
            throw new ClientStateException(
                $"The credential stored for destination {destinationId} at '{path}' is damaged — store it again "
                + "(ADR-0091, ADR-0093).");
        }
    }

    private void Write(string destinationId, Action<Utf8JsonWriter> fields)
    {
        var path = PathFor(destinationId);
        Directory.CreateDirectory(Root);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            fields(writer);
            writer.WriteEndObject();
        }

        // Owner-only before the secret is in it, then renamed into place:
        // there is no moment at which the file holds the secret and another
        // local account may read it, nor one at which a crash leaves half of it.
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
}
