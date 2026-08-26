using FallbackPlan.Repository.Format.Configuration;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The set-configuration object's two codecs (specification 11 §5.2–§5.3;
/// ADR-0047): the outer record a destination stores, and the payload sealed
/// inside it. FR-DR-006, FR-DR-007.
/// </summary>
/// <remarks>
/// The absence cases matter as much as the round trips. A repository written
/// before this revision carries no configuration object at all, and a set
/// whose recovery is unarmed is a stated condition rather than damage — so an
/// omitted schedule or retention policy has to stay legal rather than becoming
/// a decode failure years later.
/// </remarks>
[TestClass]
public sealed class SetConfigurationCodecTests
{
    private static readonly byte[] SetId = [.. Enumerable.Range(0xA0, 16).Select(value => (byte)value)];

    private static SetConfiguration FullConfiguration() => new()
    {
        SchemaVersion = SetConfiguration.CurrentSchemaVersion,
        BackupSetId = SetId,
        SetName = "documents",
        Roots =
        [
            new SetRoot("Documents", "/home/ben/Documents"),
            new SetRoot("Photos", "/mnt/media/Photos"),
        ],
        IncludeRules = ["**/*.txt"],
        ExcludeRules = ["**/node_modules/**", "**/*.tmp"],
        Schedule = "0 2 * * *",
        Retention = new SetRetention
        {
            KeepDaily = 7,
            KeepWeekly = 4,
            KeepMonthly = 12,
            MinGenerations = 3,
            DeferralDays = 30,
        },
    };

    [TestMethod]
    public void Configuration_WithEveryFieldSet_RoundTrips()
    {
        var original = FullConfiguration();

        var decoded = SetConfigurationCodec.DecodeConfiguration(
            SetConfigurationCodec.EncodeConfiguration(original));

        Assert.AreEqual(original.SetName, decoded.SetName);
        Assert.AreEqual(original.Schedule, decoded.Schedule);
        CollectionAssert.AreEqual(SetId, decoded.BackupSetId.ToArray());
        CollectionAssert.AreEqual(original.IncludeRules.ToArray(), decoded.IncludeRules.ToArray());
        CollectionAssert.AreEqual(original.ExcludeRules.ToArray(), decoded.ExcludeRules.ToArray());
        Assert.AreEqual(original.Retention, decoded.Retention);
    }

    [TestMethod]
    public void Configuration_ItsRoots_KeepTheirLabelsPathsAndOrder()
    {
        // The label is authoritative and the path is only a hint, but a hint
        // paired with the wrong label is worse than none.
        var decoded = SetConfigurationCodec.DecodeConfiguration(
            SetConfigurationCodec.EncodeConfiguration(FullConfiguration()));

        Assert.HasCount(2, decoded.Roots);
        Assert.AreEqual("Documents", decoded.Roots[0].Label);
        Assert.AreEqual("/home/ben/Documents", decoded.Roots[0].Path);
        Assert.AreEqual("Photos", decoded.Roots[1].Label);
        Assert.AreEqual("/mnt/media/Photos", decoded.Roots[1].Path);
    }

    [TestMethod]
    public void Configuration_WithNoScheduleAndNoRetention_StaysLegal()
    {
        // Manual-only, retention deferred. Absence is a policy, not damage.
        var minimal = new SetConfiguration
        {
            SchemaVersion = SetConfiguration.CurrentSchemaVersion,
            BackupSetId = SetId,
            SetName = "scratch",
        };

        var decoded = SetConfigurationCodec.DecodeConfiguration(
            SetConfigurationCodec.EncodeConfiguration(minimal));

        Assert.IsNull(decoded.Schedule);
        Assert.IsNull(decoded.Retention);
        Assert.IsEmpty(decoded.Roots);
        Assert.IsEmpty(decoded.IncludeRules);
    }

    [TestMethod]
    public void Configuration_APartialRetentionPolicy_KeepsAbsentRulesAbsent()
    {
        var partial = FullConfiguration() with
        {
            Retention = new SetRetention { MinGenerations = 5 },
        };

        var decoded = SetConfigurationCodec.DecodeConfiguration(
            SetConfigurationCodec.EncodeConfiguration(partial));

        Assert.AreEqual(5, decoded.Retention!.MinGenerations);
        Assert.IsNull(decoded.Retention.KeepDaily);
        Assert.IsNull(decoded.Retention.DeferralDays);
    }

    [TestMethod]
    public void Configuration_ABackupSetIdOfTheWrongLength_IsRefused()
    {
        var wrong = FullConfiguration() with { BackupSetId = new byte[15] };

        Assert.ThrowsExactly<ArgumentException>(() => SetConfigurationCodec.EncodeConfiguration(wrong));
    }

    // -------------------------------------------------------- outer record

    private static SetConfigurationRecord RecordOver(byte[] envelope, byte[] signature) => new()
    {
        SchemaVersion = SetConfigurationRecord.CurrentSchemaVersion,
        BackupSetId = SetId,
        RecordedAt = 1_772_000_000_000,
        SigningGeneration = 0,
        Envelope = envelope,
        Signature = signature,
    };

    [TestMethod]
    public void Record_RoundTrips()
    {
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());
        var signature = new byte[SetConfigurationRecord.SignatureLength];
        Array.Fill(signature, (byte)0x5C);

        var decoded = SetConfigurationCodec.Decode(SetConfigurationCodec.Encode(RecordOver(envelope, signature)));

        Assert.AreEqual(SetConfigurationRecord.CurrentSchemaVersion, decoded.SchemaVersion);
        Assert.AreEqual(1_772_000_000_000ul, decoded.RecordedAt);
        Assert.AreEqual(0u, decoded.SigningGeneration);
        CollectionAssert.AreEqual(SetId, decoded.BackupSetId.ToArray());
        CollectionAssert.AreEqual(envelope, decoded.Envelope.ToArray());
        CollectionAssert.AreEqual(signature, decoded.Signature.ToArray());
    }

    [TestMethod]
    public void SignedPrefix_DoesNotCoverTheSignature_SoASignerAndAVerifierAgree()
    {
        // A verifier rebuilds the prefix from the decoded record and must get
        // the same bytes the signer signed — which is only true if the
        // signature itself is outside the covered map.
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());
        var signed = RecordOver(envelope, [.. Enumerable.Repeat((byte)0x11, 64)]);
        var resigned = RecordOver(envelope, [.. Enumerable.Repeat((byte)0x22, 64)]);

        CollectionAssert.AreEqual(
            SetConfigurationCodec.EncodeSignedPrefix(signed),
            SetConfigurationCodec.EncodeSignedPrefix(resigned));
    }

    [TestMethod]
    public void SignedPrefix_TheGenerationItNames_IsCovered()
    {
        // Key 4 is what tells a verifier which signing key to derive. If it
        // were outside the signature, an attacker could redirect verification
        // to a generation of their choosing.
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());
        var atZero = RecordOver(envelope, new byte[64]);
        var atOne = atZero with { SigningGeneration = 1 };

        CollectionAssert.AreNotEqual(
            SetConfigurationCodec.EncodeSignedPrefix(atZero),
            SetConfigurationCodec.EncodeSignedPrefix(atOne));
    }

    [TestMethod]
    public void Record_ASignatureOfTheWrongLength_IsRefused()
    {
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());

        Assert.ThrowsExactly<ArgumentException>(
            () => SetConfigurationCodec.Encode(RecordOver(envelope, new byte[63])));
    }

    [TestMethod]
    public void Record_TrailingBytes_AreRefused()
    {
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());
        var encoded = SetConfigurationCodec.Encode(RecordOver(envelope, new byte[64]));

        Assert.ThrowsExactly<ManifestValidationException>(
            () => SetConfigurationCodec.Decode((byte[])[.. encoded, (byte)0x00]));
    }

    [TestMethod]
    public void Record_TruncatedBytes_AreRefusedRatherThanHalfRead()
    {
        var envelope = SetConfigurationCodec.EncodeConfiguration(FullConfiguration());
        var encoded = SetConfigurationCodec.Encode(RecordOver(envelope, new byte[64]));

        Assert.ThrowsExactly<ManifestValidationException>(
            () => SetConfigurationCodec.Decode(encoded.AsMemory(0, encoded.Length - 8)));
    }
}
