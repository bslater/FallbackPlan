using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// What a newer build leaves in a repository when it upgrades it past the
/// newest format this build implements: a format-upgrade record signed under
/// the repository's own signing key, so it verifies and moves the effective
/// version (specification 11 §5.1). This build refuses to write such a record
/// itself, so a test that needs the newer build's repository plants it here.
/// </summary>
public static class NewerBuildUpgrade
{
    /// <summary>The first format version this build does not implement.</summary>
    public static ushort UnknownVersion => (ushort)(FormatLimits.LatestFormatVersion + 1);

    /// <summary>
    /// Writes the record a newer build would write, from
    /// <paramref name="fromVersion"/> to <see cref="UnknownVersion"/>.
    /// </summary>
    /// <param name="store">The repository's store.</param>
    /// <param name="credential">The repository's write credential, whose signing key signs the record.</param>
    /// <param name="fromVersion">The effective version the newer build upgraded from.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The version the repository now writes.</returns>
    public static async ValueTask<ushort> PlantAsync(
        IObjectStore store,
        RepositoryWriteCredential credential,
        ushort fromVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(credential);

        var record = new FormatUpgradeRecord(
            fromVersion, UnknownVersion, 1_800_000_000_000, (byte[])[.. Enumerable.Repeat((byte)0x6e, 16)]);
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);
        var content = FormatUpgradeRecordCodec.Encode(
            record, signer.Sign(FormatUpgradeRecordCodec.EncodeForSigning(record)));

        var result = await store.PutAsync(
            ObjectKey.Parse(FormatUpgradeRecordCodec.KeyFor(UnknownVersion)),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        Assert.AreEqual(PutOutcome.Created, result.Outcome, "the newer build's upgrade record did not land");
        return UnknownVersion;
    }
}
