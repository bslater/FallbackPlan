using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bodu;
using FallbackPlan.Domain;

namespace FallbackPlan.Protocol;

/// <summary>
/// A one-time pairing invite's code, as one operator speaks it to another
/// (ADR-0030 Amendment 3). Twenty-one bytes under the base32: a public
/// four-byte identifier the responder looks the invite up by, the role the
/// issuer committed to, and the sixteen-byte secret that authenticates the
/// ceremony.
/// </summary>
/// <remarks>
/// The role rides <em>inside</em> the code deliberately: the redeeming side
/// derives its own declaration as the complement, so the two operators agree
/// on who stores for whom by agreeing on the code — there is no second thing
/// to coordinate, and a mismatch is impossible rather than refused.
/// </remarks>
public sealed record PairingInviteCode
{
    /// <summary>The public identifier's length in bytes.</summary>
    public const int InviteIdLength = 4;

    /// <summary>The secret's length in bytes — 128 bits, far past 01 §2.6's bar.</summary>
    public const int SecretLength = 16;

    private const int TotalLength = InviteIdLength + 1 + SecretLength;

    private PairingInviteCode(byte[] inviteId, PeerRole issuerRole, byte[] secret)
    {
        InviteId = inviteId;
        IssuerRole = issuerRole;
        Secret = secret;
    }

    /// <summary>The public lookup handle; not a secret.</summary>
    public ReadOnlyMemory<byte> InviteId { get; }

    /// <summary>The role the issuer records for whoever redeems this.</summary>
    public PeerRole IssuerRole { get; }

    /// <summary>The secret half; never persisted, never logged.</summary>
    public ReadOnlyMemory<byte> Secret { get; }

    /// <summary>The identifier as the store and the listing spell it.</summary>
    public string InviteIdHex => Convert.ToHexStringLower(InviteId.Span);

    /// <summary>Draws a fresh code.</summary>
    /// <param name="issuerRole">The role the issuer commits to recording.</param>
    /// <returns>The code.</returns>
    public static PairingInviteCode Create(PeerRole issuerRole) => new(
        RandomNumberGenerator.GetBytes(InviteIdLength), issuerRole, RandomNumberGenerator.GetBytes(SecretLength));

    /// <summary>Renders for a human to speak: lowercase base32, hyphenated in fours.</summary>
    /// <returns>The rendering <see cref="TryParse"/> accepts back.</returns>
    public string Render()
    {
        Span<byte> bytes = stackalloc byte[TotalLength];
        InviteId.Span.CopyTo(bytes);
        bytes[InviteIdLength] = (byte)IssuerRole;
        Secret.Span.CopyTo(bytes[(InviteIdLength + 1)..]);

        var flat = Base32.Encode(bytes);
        return string.Join('-', Chunk(flat, 4));
    }

    /// <summary>Parses a spoken code, forgiving the separators humans add.</summary>
    /// <param name="text">The code as typed.</param>
    /// <param name="code">The parsed code.</param>
    /// <param name="defect">Why parsing failed, when it did.</param>
    /// <returns>Whether the text was a code.</returns>
    public static bool TryParse(string? text, out PairingInviteCode? code, out string? defect)
    {
        code = null;
        defect = null;

        var compact = new string(
            (text ?? string.Empty).Trim().ToLowerInvariant()
                .Where(character => character is not ('-' or ' ')).ToArray());

        Span<byte> bytes = stackalloc byte[TotalLength];
        if (compact.Length != Base32.GetEncodedLength(TotalLength)
            || !Base32.TryDecode(compact, bytes, out var written)
            || written != TotalLength)
        {
            defect = "That is not an invite code — check it against the issuing console's line.";
            return false;
        }

        if (bytes[InviteIdLength] is not (>= 1 and <= 3))
        {
            defect = "That invite code names no storage role — it was issued by an incompatible build.";
            return false;
        }

        code = new PairingInviteCode(
            bytes[..InviteIdLength].ToArray(), (PeerRole)bytes[InviteIdLength],
            bytes[(InviteIdLength + 1)..].ToArray());
        return true;
    }

    /// <summary>Derives the invite MAC key both sides compute from the code.</summary>
    /// <returns>The 32-byte key.</returns>
    public byte[] DeriveKey() => PairingTranscript.DeriveInviteKey(Secret.Span, InviteId.Span);

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return text[offset..Math.Min(offset + size, text.Length)];
        }
    }
}

/// <summary>One issued invite, as the store holds it — the key, never the code.</summary>
/// <param name="InviteIdHex">The public identifier.</param>
/// <param name="InviteKey">The derived MAC key; sufficient to accept, insufficient to reconstruct the code's spelling.</param>
/// <param name="Label">What the redeemer will be recorded as.</param>
/// <param name="Role">The role committed at issue time.</param>
/// <param name="Terms">The terms committed at issue time (01 §4).</param>
/// <param name="CreatedAt">When it was issued, Unix milliseconds.</param>
/// <param name="ExpiresAt">When it stops being redeemable, Unix milliseconds.</param>
/// <param name="ConsumedBy">The fingerprint that redeemed it, when one has.</param>
/// <param name="ConsumedAt">When it was redeemed, Unix milliseconds.</param>
public sealed record PairingInvite(
    string InviteIdHex,
    ReadOnlyMemory<byte> InviteKey,
    string Label,
    PeerRole Role,
    PeerTerms Terms,
    ulong CreatedAt,
    ulong ExpiresAt,
    string? ConsumedBy,
    ulong? ConsumedAt)
{
    /// <summary>Whether this invite can still be redeemed at <paramref name="nowUnixMilliseconds"/>.</summary>
    /// <param name="nowUnixMilliseconds">The clock to judge by.</param>
    /// <returns>Whether it is pending.</returns>
    public bool IsPending(ulong nowUnixMilliseconds) =>
        ConsumedAt is null && nowUnixMilliseconds < ExpiresAt;
}

/// <summary>
/// The pending invites, beside the grants they become
/// (<c>&lt;state&gt;/pending-invites.json</c>; ADR-0030 Amendment 3).
/// </summary>
/// <remarks>
/// Single-use is enforced here, under the store's lock: an invite is consumed
/// exactly once, atomically, and only after the redeemer's MAC verified — a
/// failed redemption spends nothing.
/// </remarks>
public sealed class PairingInviteStore
{
    private const string FileName = "pending-invites.json";

    /// <summary>A ceiling far above use, so a bug cannot grow the file forever.</summary>
    private const int MaximumInvites = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<PairingInvite> _invites;

    private PairingInviteStore(string path, List<PairingInvite> invites)
    {
        _path = path;
        _invites = invites;
    }

    /// <summary>The invites, oldest first.</summary>
    public IReadOnlyList<PairingInvite> Invites
    {
        get
        {
            lock (_gate)
            {
                return [.. _invites];
            }
        }
    }

    /// <summary>Opens the store for a state directory, creating nothing until the first write.</summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ClientStateException">The file exists and cannot be read — loudly, like the grants.</exception>
    public static PairingInviteStore Open(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);

        var path = Path.Combine(stateDirectory, FileName);
        if (!File.Exists(path))
        {
            return new PairingInviteStore(path, []);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredInvite>>(File.ReadAllText(path), SerializerOptions) ?? [];
            return new PairingInviteStore(path, [.. stored.Select(invite => invite.ToInvite())]);
        }
        catch (JsonException exception)
        {
            throw new ClientStateException(
                $"'{path}' does not parse as a pending-invite store: {exception.Message}. "
                + "Remove it to start empty — pending invites are re-issuable, unlike grants.");
        }
    }

    /// <summary>Issues a fresh invite and persists it.</summary>
    /// <param name="label">What the redeemer will be recorded as.</param>
    /// <param name="role">The role committed now, at issue time.</param>
    /// <param name="terms">The terms committed now.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="timeToLiveMilliseconds">How long it stays redeemable.</param>
    /// <returns>The code — the only time it exists — and the stored invite.</returns>
    public (PairingInviteCode Code, PairingInvite Invite) Issue(
        string label, PeerRole role, PeerTerms terms, ulong nowUnixMilliseconds, ulong timeToLiveMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(label);

        var code = PairingInviteCode.Create(role);
        var invite = new PairingInvite(
            code.InviteIdHex, code.DeriveKey(), label, role, terms,
            nowUnixMilliseconds, nowUnixMilliseconds + timeToLiveMilliseconds, null, null);

        lock (_gate)
        {
            if (_invites.Count(candidate => candidate.IsPending(nowUnixMilliseconds)) >= MaximumInvites)
            {
                throw new ClientStateException(
                    $"There are already {MaximumInvites} pending invites; revoke some before issuing more.");
            }

            // Issuing is also the tidy point: an invite that expired or was
            // consumed no longer guards anything and need not be kept.
            _invites = [.. _invites.Where(candidate => candidate.IsPending(nowUnixMilliseconds)), invite];
            Persist();
        }

        return (code, invite);
    }

    /// <summary>Whether any invite could still be redeemed.</summary>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <returns>Whether one is pending.</returns>
    public bool HasPending(ulong nowUnixMilliseconds)
    {
        lock (_gate)
        {
            return _invites.Any(invite => invite.IsPending(nowUnixMilliseconds));
        }
    }

    /// <summary>Finds a pending invite by its public identifier.</summary>
    /// <param name="inviteId">The identifier from the offer.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <returns>The invite, or null — unknown, expired and consumed alike.</returns>
    public PairingInvite? FindPending(ReadOnlySpan<byte> inviteId, ulong nowUnixMilliseconds)
    {
        var hex = Convert.ToHexStringLower(inviteId);
        lock (_gate)
        {
            return _invites.FirstOrDefault(invite =>
                string.Equals(invite.InviteIdHex, hex, StringComparison.Ordinal)
                && invite.IsPending(nowUnixMilliseconds));
        }
    }

    /// <summary>Consumes an invite — first redeemer wins, exactly once.</summary>
    /// <param name="inviteIdHex">The invite's identifier.</param>
    /// <param name="fingerprint">Who redeemed it.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <returns>Whether this call was the consumption.</returns>
    public bool Consume(string inviteIdHex, string fingerprint, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(inviteIdHex);
        ThrowHelper.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            var index = _invites.FindIndex(invite =>
                string.Equals(invite.InviteIdHex, inviteIdHex, StringComparison.Ordinal)
                && invite.IsPending(nowUnixMilliseconds));
            if (index < 0)
            {
                return false;
            }

            _invites[index] = _invites[index] with { ConsumedBy = fingerprint, ConsumedAt = nowUnixMilliseconds };
            Persist();
            return true;
        }
    }

    /// <summary>Removes an invite outright.</summary>
    /// <param name="inviteIdHex">The invite's identifier.</param>
    /// <returns>Whether anything was removed.</returns>
    public bool Revoke(string inviteIdHex)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(inviteIdHex);

        lock (_gate)
        {
            var removed = _invites.RemoveAll(invite =>
                string.Equals(invite.InviteIdHex, inviteIdHex, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Persist() =>
        AtomicFile.WriteAllText(
            _path, JsonSerializer.Serialize(_invites.Select(StoredInvite.From).ToList(), SerializerOptions));

    private sealed record StoredInvite
    {
        [JsonPropertyName("invite_id")]
        public required string InviteId { get; init; }

        [JsonPropertyName("invite_key")]
        public required string InviteKey { get; init; }

        [JsonPropertyName("label")]
        public required string Label { get; init; }

        [JsonPropertyName("role")]
        public required byte Role { get; init; }

        [JsonPropertyName("quota_bytes")]
        public ulong QuotaBytes { get; init; }

        [JsonPropertyName("schedule_window")]
        public string ScheduleWindow { get; init; } = string.Empty;

        [JsonPropertyName("retention_floor_generations")]
        public uint RetentionFloorGenerations { get; init; }

        [JsonPropertyName("created_at")]
        public required ulong CreatedAt { get; init; }

        [JsonPropertyName("expires_at")]
        public required ulong ExpiresAt { get; init; }

        [JsonPropertyName("consumed_by")]
        public string? ConsumedBy { get; init; }

        [JsonPropertyName("consumed_at")]
        public ulong? ConsumedAt { get; init; }

        public static StoredInvite From(PairingInvite invite) => new()
        {
            InviteId = invite.InviteIdHex,
            InviteKey = Convert.ToHexStringLower(invite.InviteKey.Span),
            Label = invite.Label,
            Role = (byte)invite.Role,
            QuotaBytes = invite.Terms.QuotaBytes,
            ScheduleWindow = invite.Terms.ScheduleWindow,
            RetentionFloorGenerations = invite.Terms.RetentionFloorGenerations,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt,
            ConsumedBy = invite.ConsumedBy,
            ConsumedAt = invite.ConsumedAt,
        };

        public PairingInvite ToInvite() => new(
            InviteId, Convert.FromHexString(InviteKey), Label,
            Role is >= 1 and <= 3 ? (PeerRole)Role : PeerRole.StoresForUs,
            new PeerTerms(QuotaBytes, ScheduleWindow, RetentionFloorGenerations),
            CreatedAt, ExpiresAt, ConsumedBy, ConsumedAt);
    }
}
