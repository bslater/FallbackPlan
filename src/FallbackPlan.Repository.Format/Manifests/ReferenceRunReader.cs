using System.Runtime.CompilerServices;
using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// Reads a file version's reference runs in its order, each held to the
/// manifest as it arrives (ADR-0202 decision 4). Every reader of a version's
/// references follows its runs through here, so that none of them takes a run
/// another would refuse, and none treats a version as read before its last
/// run is.
/// </summary>
public static class ReferenceRunReader
{
    /// <summary>
    /// The runs <paramref name="manifest"/> lists, read through
    /// <paramref name="read"/> one at a time, so that a reader holds one run's
    /// references however many the file has. A manifest listing none yields
    /// none: its references and holes are its own.
    /// </summary>
    /// <param name="manifest">The version whose runs to read.</param>
    /// <param name="read">Reads a run's plaintext by its object identifier, or answers null when it cannot.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <exception cref="ReferenceRunMissingException">A run did not read. No run after it is read.</exception>
    /// <exception cref="ManifestValidationException">
    /// A run is not one ADR-0202 allows, does not start where the one before it
    /// ended, is not full though another follows it, or the runs do not end at
    /// the file's length.
    /// </exception>
    public static IAsyncEnumerable<ReferenceRun> ReadAsync(
        FileVersionManifest manifest,
        Func<ObjectId, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> read,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(manifest);
        ThrowHelper.ThrowIfNull(read);
        return ReadCoreAsync(manifest, read, cancellationToken);
    }

    private static async IAsyncEnumerable<ReferenceRun> ReadCoreAsync(
        FileVersionManifest manifest,
        Func<ObjectId, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runs = manifest.ReferenceRuns;
        if (runs.Count == 0)
        {
            yield break;
        }

        var sequence = new ReferenceRunCodec.Sequence(manifest);
        for (var index = 0; index < runs.Count; index++)
        {
            var plaintext = await read(runs[index], cancellationToken).ConfigureAwait(false)
                ?? throw new ReferenceRunMissingException(
                    Strings.FormatReferenceRunReader_RunDidNotRead(runs[index], index, runs.Count), runs[index], index);
            var run = ReferenceRunCodec.Decode(plaintext);
            sequence.Next(run);
            yield return run;
        }

        sequence.End();
    }
}
