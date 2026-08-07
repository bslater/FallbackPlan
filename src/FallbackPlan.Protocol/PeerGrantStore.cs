using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Application;

namespace FallbackPlan.Protocol;

/// <summary>What a peer is permitted to do with this device (specification peer-protocol 01 §3).</summary>
public enum PeerRole : byte
{
    /// <summary>This peer may store objects here — we are its destination.</summary>
    StoresHere = 1,

    /// <summary>This peer stores objects for us — it is our destination.</summary>
    StoresForUs = 2,

    /// <summary>Both directions.</summary>
    Both = 3,
}

/// <summary>
/// One pinned pairing (specification peer-protocol 01 §3).
/// </summary>
/// <param name="Identity">The pinned peer key. Changing it is not an edit — it is a different peer.</param>
/// <param name="Label">Human-chosen, freely editable, carrying no authority.</param>
/// <param name="Role">Which direction, or both.</param>
/// <param name="Terms">The destination's terms, per 01 §4.</param>
/// <param name="PairedAtUnixMilliseconds">When it was approved. Informational.</param>
public sealed record PeerGrant(
    PeerIdentity Identity,
    string Label,
    PeerRole Role,
    PeerTerms Terms,
    ulong PairedAtUnixMilliseconds);

/// <summary>
/// The pinned pairings this device holds (specification peer-protocol 01 §3).
/// </summary>
/// <remarks>
/// <para>
/// Durable local state, and NFR-REL-007
/// names it explicitly alongside the device keypair: not rebuildable from the
/// repository, so it is stored apart from the disposable catalogue and lost only
/// with the device's identity.
/// </para>
/// <para>
/// <b>Lookup is by full key, never by fingerprint or label.</b> That is what
/// makes pinning mean anything: a peer presenting a key this store does not hold
/// is simply not found, and 01 §2.5's refusal follows from the absence rather
/// than from a comparison someone had to remember to write.
/// </para>
/// </remarks>
public sealed class PeerGrantStore
{
    /// <summary>The most grants one device will hold ([00 §2.3](../../specifications/peer-protocol/00-conventions.md)).</summary>
    public const int MaximumGrants = 1_024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<string, PeerGrant> _grants;

    // One store, several readers: a session handler resolves a grant while an
    // operator revokes one. The job journal taught this lesson the expensive
    // way — an unsynchronised list plus a save that serialises it loses writes
    // whose order the disk decides.
    private readonly Lock _gate = new();

    private PeerGrantStore(string path, Dictionary<string, PeerGrant> grants)
    {
        _path = path;
        _grants = grants;
    }

    /// <summary>Every grant, in no particular order.</summary>
    /// <remarks>A snapshot, not a view.</remarks>
    public IReadOnlyList<PeerGrant> Grants
    {
        get
        {
            lock (_gate)
            {
                return [.. _grants.Values];
            }
        }
    }

    /// <summary>Opens (or creates) the grant store in <paramref name="stateDirectory"/>.</summary>
    /// <param name="stateDirectory">The device's durable local state directory.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ClientStateException">The file exists and is not readable as grants.</exception>
    public static PeerGrantStore Open(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "peers.json");

        if (!File.Exists(path))
        {
            return new PeerGrantStore(path, []);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredGrant>>(File.ReadAllText(path), SerializerOptions) ?? [];
            var grants = new Dictionary<string, PeerGrant>(StringComparer.Ordinal);

            foreach (var entry in stored)
            {
                var grant = entry.ToGrant();
                grants[Key(grant.Identity)] = grant;
            }

            return new PeerGrantStore(path, grants);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            // Refused loudly, never quietly emptied. A corrupt job journal costs
            // history; a corrupt grant file costs every pairing this device
            // holds, and starting empty would silently unpair a fleet.
            throw new ClientStateException(
                $"The peer grants at '{path}' could not be read. Pairings are not rebuildable — "
                + "restore the file or re-pair deliberately.", exception);
        }
    }

    /// <summary>Records an approved pairing, replacing any grant for the same key.</summary>
    /// <param name="grant">The grant to store.</param>
    /// <exception cref="ClientStateException">The store is full.</exception>
    public void Pin(PeerGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        lock (_gate)
        {
            if (!_grants.ContainsKey(Key(grant.Identity)) && _grants.Count >= MaximumGrants)
            {
                throw new ClientStateException(
                    $"This device already holds {MaximumGrants} peer grants, which is the limit.");
            }

            _grants[Key(grant.Identity)] = grant;
            Save();
        }
    }

    /// <summary>Finds the grant for a peer, by its full key.</summary>
    /// <param name="identity">The identity a peer presented.</param>
    /// <returns>The grant, or <see langword="null"/> when this peer is not paired.</returns>
    public PeerGrant? Find(PeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            return _grants.GetValueOrDefault(Key(identity));
        }
    }

    /// <summary>Removes a pairing.</summary>
    /// <param name="identity">The peer to unpair.</param>
    /// <returns><see langword="true"/> when a grant was there to remove.</returns>
    /// <remarks>
    /// Unilateral and immediate locally; it takes effect for the peer at its
    /// next session. It reaches across no network and deletes nothing already
    /// stored elsewhere — what a destination does with objects it already holds
    /// after being revoked is its own policy, and this specification does not
    /// pretend otherwise.
    /// </remarks>
    public bool Revoke(PeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (!_grants.Remove(Key(identity)))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>Replaces a grant's label, which carries no authority.</summary>
    /// <param name="identity">The peer to rename.</param>
    /// <param name="label">The new label.</param>
    /// <returns><see langword="true"/> when the peer was found.</returns>
    public bool Relabel(PeerIdentity identity, string label)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(label);

        lock (_gate)
        {
            if (!_grants.TryGetValue(Key(identity), out var grant))
            {
                return false;
            }

            _grants[Key(identity)] = grant with { Label = label };
            Save();
            return true;
        }
    }

    /// <summary>
    /// Applies terms a destination has changed, and says whether they narrowed.
    /// </summary>
    /// <param name="identity">The destination.</param>
    /// <param name="terms">The terms it now offers.</param>
    /// <returns>
    /// <see langword="true"/> when the new terms narrow what was in force, which
    /// the caller reports as degraded rather than letting replication stall
    /// without explanation (01 §4).
    /// </returns>
    /// <exception cref="ClientStateException">The peer is not paired.</exception>
    public bool ApplyTerms(PeerIdentity identity, PeerTerms terms)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(terms);

        lock (_gate)
        {
            if (!_grants.TryGetValue(Key(identity), out var grant))
            {
                throw new ClientStateException($"No grant exists for peer {identity.Fingerprint}.");
            }

            var narrowed = terms.Narrows(grant.Terms);
            _grants[Key(identity)] = grant with { Terms = terms };
            Save();
            return narrowed;
        }
    }

    /// <summary>The key a grant is indexed by: the full public key, hex-encoded.</summary>
    private static string Key(PeerIdentity identity) => Convert.ToHexString(identity.PublicKey);

    /// <summary>Serialises and writes. Call under <c>_gate</c>.</summary>
    private void Save() =>
        AtomicFile.WriteAllText(
            _path, JsonSerializer.Serialize(_grants.Values.Select(StoredGrant.From).ToList(), SerializerOptions));

    /// <summary>The on-disk shape. Separate from the domain record so the file format is explicit.</summary>
    private sealed record StoredGrant
    {
        [JsonPropertyName("peer_identity")]
        public required string PeerIdentity { get; init; }

        [JsonPropertyName("label")]
        public required string Label { get; init; }

        [JsonPropertyName("role")]
        public required PeerRole Role { get; init; }

        [JsonPropertyName("quota_bytes")]
        public required ulong QuotaBytes { get; init; }

        [JsonPropertyName("schedule_window")]
        public required string ScheduleWindow { get; init; }

        [JsonPropertyName("retention_floor_generations")]
        public required uint RetentionFloorGenerations { get; init; }

        [JsonPropertyName("paired_at")]
        public required ulong PairedAt { get; init; }

        public static StoredGrant From(PeerGrant grant) => new()
        {
            PeerIdentity = Convert.ToHexString(grant.Identity.PublicKey),
            Label = grant.Label,
            Role = grant.Role,
            QuotaBytes = grant.Terms.QuotaBytes,
            ScheduleWindow = grant.Terms.ScheduleWindow,
            RetentionFloorGenerations = grant.Terms.RetentionFloorGenerations,
            PairedAt = grant.PairedAtUnixMilliseconds,
        };

        public PeerGrant ToGrant() => new(
            Protocol.PeerIdentity.FromPublicKey(Convert.FromHexString(PeerIdentity)),
            Label,
            Role,
            new PeerTerms(QuotaBytes, ScheduleWindow, RetentionFloorGenerations),
            PairedAt);
    }
}
