using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FallbackPlan.Api;

/// <summary>
/// The <b>public</b> half of an installation's key derivation, recorded in
/// its state directory: the Argon2id salt and parameters every archive of
/// this installation stamps into its own descriptor, and the sealing public
/// key that proves a re-derivation reproduced them.
/// </summary>
/// <remarks>
/// <para>
/// It exists so a recovery kit can be rebuilt from the moment the passphrase
/// is chosen, which is what a v2 kit describes
/// (<c>specifications/recovery-kit</c> §2.2, FR-KIT-004). Before it, the
/// console could only learn the salt by reading a repository descriptor, and
/// an installation has none until its first backup — so an operator who
/// closed the tab before saving the kit was stranded behind the full-screen
/// setup gate, unable to reach the configuration that would let them run the
/// backup that would write the descriptor.
/// </para>
/// <para>
/// <b>Nothing here is secret, and nothing here crosses the command
/// surface.</b> Both are deliberate. Every one of these three facts is
/// already published by every archive descriptor this installation writes,
/// so recording them adds no exposure a first backup would not — and the
/// file lives in the state directory the service already protects, beside
/// the sealed credential it is derived from. Putting them on
/// <c>describe_service</c> instead would have been the smaller change and
/// the wrong one: the only other field a v2 kit carries is the issuing
/// device id, which that result already publishes, so a client holding
/// nothing but a session could have assembled a whole kit — and NFR-SEC-009
/// and <c>threat-model</c> T-19 both turn on holding a running service
/// <i>not</i> being sufficient to produce one.
/// </para>
/// <para>
/// Shared here, in the contract assembly, for the same reason
/// <see cref="InstallationDefaults"/> is: the service writes it and a
/// console on the same machine reads it, so one spelling of the layout has
/// to serve both.
/// </para>
/// </remarks>
/// <param name="KdfSalt">The installation's 16-byte Argon2id salt, lowercase hex.</param>
/// <param name="KdfMemoryKiB">Argon2id memory cost, KiB.</param>
/// <param name="KdfIterations">Argon2id time cost.</param>
/// <param name="KdfParallelism">Argon2id lanes.</param>
/// <param name="SealingPublicKey">The derived X25519 public key, lowercase hex — the verifier.</param>
public sealed record InstallationParameters(
    string KdfSalt,
    uint KdfMemoryKiB,
    uint KdfIterations,
    byte KdfParallelism,
    string SealingPublicKey)
{
    /// <summary>The file's name inside the state directory.</summary>
    public const string FileName = "installation-public.json";

    /// <summary>How long the salt is, in bytes, before hex.</summary>
    private const int SaltLength = 16;

    /// <summary>How long the sealing public key is, in bytes, before hex.</summary>
    private const int PublicKeyLength = 32;

    /// <summary>The note a person finding this file deserves.</summary>
    private const string Note =
        "The PUBLIC half of this installation's key derivation. Every archive it writes records the same "
        + "three facts in its own descriptor. It contains no passphrase and no private key, and it opens "
        + "nothing on its own — it is what lets this installation's recovery kit be rebuilt before any "
        + "backup has run.";

    /// <summary>Where the file sits for a given state directory.</summary>
    /// <param name="stateDirectory">The installation's state directory.</param>
    /// <returns>The full path.</returns>
    public static string PathIn(string stateDirectory) =>
        Path.Combine(stateDirectory, FileName);

    /// <summary>
    /// Reads the recorded parameters, or null when there are none to read.
    /// </summary>
    /// <remarks>
    /// A missing, unreadable or malformed file answers null rather than
    /// throwing: every caller has a descriptor to fall back to, and a
    /// half-written file must degrade to "ask an archive" rather than break
    /// a ceremony.
    /// </remarks>
    /// <param name="stateDirectory">The installation's state directory.</param>
    /// <returns>The parameters, or null.</returns>
    public static InstallationParameters? TryLoad(string? stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(PathIn(stateDirectory));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            var salt = root.GetProperty("kdf_salt").GetString();
            var sealingKey = root.GetProperty("sealing_public_key").GetString();
            if (!IsHex(salt, SaltLength) || !IsHex(sealingKey, PublicKeyLength))
            {
                return null;
            }

            return new InstallationParameters(
                salt!.ToLowerInvariant(),
                root.GetProperty("kdf_memory_kib").GetUInt32(),
                root.GetProperty("kdf_iterations").GetUInt32(),
                root.GetProperty("kdf_parallelism").GetByte(),
                sealingKey!.ToLowerInvariant());
        }
        catch (Exception malformed) when (malformed is JsonException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Records the parameters, replacing anything already there.</summary>
    /// <param name="stateDirectory">The installation's state directory.</param>
    public void Save(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);

        var buffer = new ArrayBufferWriter<byte>();

        // The relaxed encoder, because the note is meant to be read. The
        // default turns every apostrophe and dash in it into a \uXXXX
        // escape — the right caution for JSON bound for a browser, and
        // noise in a file whose whole job is to explain itself to whoever
        // finds it. Nothing written here is ever embedded in markup.
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("kdf_salt", KdfSalt);
            writer.WriteNumber("kdf_memory_kib", KdfMemoryKiB);
            writer.WriteNumber("kdf_iterations", KdfIterations);
            writer.WriteNumber("kdf_parallelism", KdfParallelism);
            writer.WriteString("sealing_public_key", SealingPublicKey);
            writer.WriteString("note", Note);
            writer.WriteEndObject();
        }

        // Written whole and renamed into place: a reader that meets a torn
        // file falls back to hunting descriptors, and this is what keeps
        // that fallback rare rather than routine.
        var path = PathIn(stateDirectory);
        var temporary = string.Create(
            CultureInfo.InvariantCulture, $"{path}.{Environment.ProcessId}.tmp");
        File.WriteAllBytes(temporary, buffer.WrittenSpan.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Whether <paramref name="text"/> is hex of exactly <paramref name="bytes"/> bytes.</summary>
    /// <param name="text">The candidate.</param>
    /// <param name="bytes">How many bytes it must encode.</param>
    /// <returns><see langword="true"/> when it is usable.</returns>
    private static bool IsHex(string? text, int bytes) =>
        text is not null
        && text.Length == bytes * 2
        && text.All(character => char.IsAsciiHexDigit(character));
}
