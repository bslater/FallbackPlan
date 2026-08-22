using Bodu;
using Bodu.Security.Cryptography;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Repository.Crypto.Resources;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// A stored account password, as a memory-hard one-way hash with its salt and
/// parameters encoded beside it (FR-USR-002, NFR-SEC-012;
/// <see href="../../docs/adr/0045-client-authentication.md">ADR-0045</see> §4).
/// </summary>
/// <remarks>
/// <para>
/// This lives in the cryptography assembly rather than beside the account
/// store that uses it, and the reason is worth knowing: the dependency rules
/// confine <c>Bodu.Security.Cryptography</c> to an allowlist of exactly two
/// assemblies and say that adding a third "requires an argument". Rather than
/// make that argument, the primitive goes where Argon2id already lives and is
/// cross-verified against published vectors on every CI run, and the Agent
/// keeps the store and the policy — the same split
/// <see cref="KekDerivation"/> already has. The cost is that the standalone
/// recovery tool links a class it never calls.
/// </para>
/// <para>
/// The parameters are stored with the hash rather than assumed, because they
/// are facts about how a password was hashed rather than policy about how one
/// should be: raising the cost for new accounts must not lock out the accounts
/// that already exist. That is the same rule specification 03 §2 states for
/// repository KDF parameters, applied here for the same reason.
/// </para>
/// <para>
/// <see cref="ToString"/> is redacted, because redaction is by declared type
/// (NFR-SEC-006) and a password hash is offline-attackable material: it is not
/// the password, but it is the thing an attacker takes away to become the
/// password at leisure.
/// </para>
/// </remarks>
public sealed class PasswordHash
{
    /// <summary>
    /// The encoding's leading field, so a stored line is self-describing and a
    /// future scheme is a parse arm rather than a guess.
    /// </summary>
    private const string Scheme = "fbp-argon2id";

    /// <summary>The hash length: 32 bytes.</summary>
    public const int TagLength = 32;

    /// <summary>The per-account salt length: 16 bytes.</summary>
    public const int SaltLength = 16;

    private readonly byte[] _salt;
    private readonly byte[] _tag;

    private PasswordHash(KdfParameters parameters, byte[] salt, byte[] tag)
    {
        Parameters = parameters;
        _salt = salt;
        _tag = tag;
    }

    /// <summary>The parameters this hash was produced under.</summary>
    public KdfParameters Parameters { get; }

    /// <summary>
    /// Hashes <paramref name="password"/> under a freshly drawn salt.
    /// </summary>
    /// <param name="password">The password as typed. Normalised to NFC, as a passphrase is, so the same password typed on two operating systems verifies.</param>
    /// <param name="parameters">The cost parameters, or specification 03 §2's creation minimums, which this product also uses as its defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="password"/> is empty.</exception>
    public static PasswordHash Create(string password, KdfParameters? parameters = null)
    {
        ThrowHelper.ThrowIfNull(password);

        if (password.Length == 0)
        {
            throw new ArgumentException(Strings.PasswordHash_EmptyPasswordRefused, nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var chosen = parameters ?? KdfParameters.CreationMinimums;

        return new PasswordHash(chosen, salt, Derive(password, chosen, salt));
    }

    /// <summary>
    /// Whether <paramref name="password"/> is the one this hash was made from.
    /// </summary>
    /// <remarks>
    /// The comparison is constant-time, which matters less here than it does
    /// for a MAC — the attacker already controls the input and pays the full
    /// memory-hard cost per guess — and costs nothing, so there is no reason
    /// to leave the timing signal lying around.
    /// </remarks>
    public bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var candidate = Derive(password, Parameters, _salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(candidate, _tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>
    /// The stored form: <c>fbp-argon2id$m=&lt;kib&gt;,t=&lt;iterations&gt;,p=&lt;lanes&gt;$&lt;salt&gt;$&lt;tag&gt;</c>,
    /// the last two lower-case hex.
    /// </summary>
    /// <remarks>
    /// Deliberately not a bare hash: a stored hash with no parameters beside it
    /// can only be verified by an implementation that happens to still use the
    /// same cost, which makes raising the cost a migration rather than a
    /// setting. Hex rather than base64 for the same reason every other
    /// identifier in this product is hex — one encoding to recognise.
    /// </remarks>
    public string Encode() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Scheme}$m={Parameters.MemoryKiB},t={Parameters.Iterations},p={Parameters.Parallelism}$" +
        $"{Convert.ToHexStringLower(_salt)}${Convert.ToHexStringLower(_tag)}");

    /// <summary>
    /// Parses a stored form. Anything unrecognised, malformed or the wrong
    /// length answers <see langword="false"/> rather than throwing: a
    /// tampered credential file is an authentication failure, not a crash.
    /// </summary>
    public static bool TryParse(string? encoded, out PasswordHash? hash)
    {
        hash = null;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var fields = encoded.Split('$');
        if (fields.Length != 4 || !string.Equals(fields[0], Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseParameters(fields[1], out var parameters)
            || !TryParseHex(fields[2], SaltLength, out var salt)
            || !TryParseHex(fields[3], TagLength, out var tag))
        {
            return false;
        }

        hash = new PasswordHash(parameters!, salt!, tag!);
        return true;
    }

    /// <summary>
    /// Deliberately redacted: a password hash must never reach a log, a
    /// command result, or a diagnostic (FR-USR-002, NFR-SEC-006). The stored
    /// form is available through <see cref="Encode"/>, which a call site has
    /// to ask for by name.
    /// </summary>
    public override string ToString() => "password-hash(redacted)";

    private static byte[] Derive(string password, KdfParameters parameters, byte[] salt)
    {
        // NFC for the same reason a passphrase is normalised: the same
        // characters typed on two operating systems can differ in composition,
        // and an account that will not accept its own password on a second
        // machine is indistinguishable from a wrong password.
        var normalised = password.IsNormalized(NormalizationForm.FormC)
            ? password
            : password.Normalize(NormalizationForm.FormC);

        var utf8 = Encoding.UTF8.GetBytes(normalised);
        try
        {
            return Argon2id.DeriveKey(
                utf8,
                salt,
                new Bodu.Security.Cryptography.Argon2Parameters
                {
                    MemoryKiB = checked((int)parameters.MemoryKiB),
                    Iterations = checked((int)parameters.Iterations),
                    Parallelism = parameters.Parallelism,
                    TagLength = TagLength,
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static bool TryParseParameters(string field, out KdfParameters? parameters)
    {
        parameters = null;

        var parts = field.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseNamed(parts[0], "m=", out var memoryKiB)
            || !TryParseNamed(parts[1], "t=", out var iterations)
            || !TryParseNamed(parts[2], "p=", out var parallelism)
            || parallelism is 0 or > byte.MaxValue
            || memoryKiB == 0
            || iterations == 0)
        {
            return false;
        }

        parameters = new KdfParameters
        {
            MemoryKiB = memoryKiB,
            Iterations = iterations,
            Parallelism = (byte)parallelism,
        };

        return true;
    }

    private static bool TryParseNamed(string field, string prefix, out uint value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal)
            && uint.TryParse(
                field.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHex(string field, int expectedBytes, out byte[]? bytes)
    {
        bytes = null;

        if (field.Length != expectedBytes * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(field);
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }
}
