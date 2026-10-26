using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// A peer's replica as the target of a repair
/// ([ADR-0100](../../docs/adr/0100-a-peer-replaces-what-its-own-check-finds-damaged.md)):
/// a copy proven sound here is offered over a repair session, sent only if
/// the peer's own check of its copy asks for it, and proven again where it
/// landed over the retrieval session. The peer's verdict is a refusal or a
/// claim; only the read-back is a proof.
/// </summary>
/// <remarks>
/// Each session is dialled on first need and kept for the target's life, so
/// a repair of several blobs costs the peer one handshake of each kind, and
/// a repair that finds nothing to offer costs it none.
/// </remarks>
internal sealed class PeerRepairTarget : IRepairTarget, IAsyncDisposable
{
    private readonly ServiceRuntime _runtime;
    private readonly DestinationConfiguration _peer;
    private readonly ReadOnlyMemory<byte> _repositoryId;
    private readonly ByteRateLimiter? _limiter;
    private PeerRepairClient? _repair;
    private PeerRetrievalClient? _retrieval;
    private IObjectStore? _landed;

    /// <summary>Creates the target.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="peer">The peer holding the damage.</param>
    /// <param name="repositoryId">The replica's repository.</param>
    /// <param name="limiter">The peer's transfer limit, which both sessions cross; null for none.</param>
    /// <param name="landed">
    /// A retrieval session's store already open on the replica, to prove
    /// what lands there; null to dial one when the first copy is taken.
    /// </param>
    public PeerRepairTarget(
        ServiceRuntime runtime, DestinationConfiguration peer, ReadOnlyMemory<byte> repositoryId,
        ByteRateLimiter? limiter, IObjectStore? landed)
    {
        _runtime = runtime;
        _peer = peer;
        _repositoryId = repositoryId;
        _limiter = limiter;
        _landed = landed;
    }

    private string Name => SetCopies.NameOf(_peer);

    /// <inheritdoc/>
    public async ValueTask<RepairInstall> InstallAsync(
        ObjectKey key, IObjectStore candidate, long length, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(candidate);

        RepairVerdictCode verdict;
        try
        {
            _repair ??= await PeerRepairClient.DialAsync(_runtime, _peer, _repositoryId, _limiter, cancellationToken)
                .ConfigureAwait(false);
            verdict = await _repair.OfferAsync(key.Value, length, cancellationToken).ConfigureAwait(false);
            if (verdict == RepairVerdictCode.Send)
            {
                await using var content = await ReplicaRepair.OpenAsync(candidate, key, cancellationToken)
                    .ConfigureAwait(false);
                verdict = await _repair.SendAsync(key.Value, content, length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception
            is PeerProtocolException or IOException or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException)
        {
            // Nothing is known of the peer's copy after a session that broke,
            // so the key stays held and the next sync offers it again; the
            // broken session is not offered the next key.
            if (_repair is not null)
            {
                await _repair.DisposeAsync().ConfigureAwait(false);
                _repair = null;
            }

            return RepairInstall.Refused($"{Name} could not be asked to take a replacement: {exception.Message}");
        }

        return verdict switch
        {
            RepairVerdictCode.Replaced => RepairInstall.Done,
            RepairVerdictCode.NotHeld => RepairInstall.NoLongerHeld,
            RepairVerdictCode.Intact => RepairInstall.Refused(
                $"{Name} would not take a replacement: its own check finds its copy whole, so what it holds is a "
                + "well-formed blob other than the one sealed under that key, which only the repository's keys can tell"),
            RepairVerdictCode.LengthDiffers => RepairInstall.Refused(
                $"{Name} would not take a replacement: its copy is not the length of the blob that was sealed, so "
                + "nothing left on it says which blob a replacement must be"),
            _ => RepairInstall.Refused(
                $"{Name} would not take a replacement: its copy no longer states the locator of the blob that was "
                + "sealed, so the damage reaches the locator itself"),
        };
    }

    /// <inheritdoc/>
    public async ValueTask<IObjectStore> LandedAsync(CancellationToken cancellationToken)
    {
        if (_landed is null)
        {
            _retrieval = await PeerRetrievalClient.DialAsync(_runtime, _peer, _repositoryId, cancellationToken)
                .ConfigureAwait(false);
            _landed = PacedObjectStore.Over(new PeerRetrievalObjectStore(_retrieval), _limiter);
        }

        return _landed;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_repair is not null)
        {
            await _repair.DisposeAsync().ConfigureAwait(false);
        }

        if (_retrieval is not null)
        {
            await _retrieval.DisposeAsync().ConfigureAwait(false);
        }
    }
}
