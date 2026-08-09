using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// Journal records (specification 08 §2–§8; FR-GC-001, FR-SNP-005): every
/// kind round-trips through the two-pass signature, verification descends
/// generations because the record carries none, expiry demands BOTH
/// conditions, and the collector's survey treats unparseable intents as
/// live.
/// </summary>
[TestClass]
public sealed class JournalTests
{
    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static readonly byte[] BackupSet = [.. Enumerable.Repeat((byte)0x33, 16)];

    private static BlobId Blob(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return BlobId.FromBytes(bytes);
    }

    public static IEnumerable<object[]> EveryKind() =>
    [
        [new JournalRecord(JournalRecordKind.WriteIntent, Writer, 1, 1000, new JournalPayload.WriteIntent(BackupSet, [Blob(1), Blob(2)], 60_000, 5, IntentPurpose.Backup))],
        [new JournalRecord(JournalRecordKind.IntentExtension, Writer, 2, 1500, new JournalPayload.IntentExtension(1, [Blob(3)], 90_000))],
        [new JournalRecord(JournalRecordKind.IntentRetirement, Writer, 3, 2000, new JournalPayload.IntentRetirement(1, IntentOutcome.Completed))],
        [new JournalRecord(JournalRecordKind.Audit, Writer, 4, 2500, new JournalPayload.Audit(AuditAction.GcPass, "operator@example", 42))],
    ];

    [TestMethod]
    [DynamicData(nameof(EveryKind))]
    public void Every_record_kind_round_trips_through_the_two_pass_signature(JournalRecord record)
    {
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var signer = RepositorySigner.Create(hierarchy, new KeyGeneration(1));

        var signedBytes = JournalRecordCodec.EncodeForSigning(record);
        var stored = JournalRecordCodec.Encode(record, signer.Sign(signedBytes));

        var decoded = JournalRecordCodec.Decode(stored);

        SequenceAssert.AreEqual(signedBytes, decoded.SignedBytes.ToArray());
        Assert.AreEqual(record.Kind, decoded.Record.Kind);
        Assert.AreEqual(record.Sequence, decoded.Record.Sequence);
        Assert.AreEqual(record.Payload, decoded.Record.Payload,
            EqualityComparer<JournalPayload>.Create((left, right) =>
                left is not null && right is not null &&
                JournalRecordCodec.EncodeForSigning(record with { Payload = left })
                    .SequenceEqual(JournalRecordCodec.EncodeForSigning(record with { Payload = right }))));
    }

    [TestMethod]
    public void Verification_descends_generations_and_finds_the_signing_one()
    {
        using var hierarchy = new KeyHierarchy(MasterKey);

        var record = new JournalRecord(JournalRecordKind.WriteIntent, Writer, 1, 1000,
            new JournalPayload.WriteIntent(BackupSet, [], 60_000, 5, IntentPurpose.Backup));
        var signedBytes = JournalRecordCodec.EncodeForSigning(record);

        byte[] signature;
        using (var signer = RepositorySigner.Create(hierarchy, new KeyGeneration(1)))
        {
            signature = signer.Sign(signedBytes);
        }

        // The record has no generation field (08 §2 key 6): the reader tries
        // the current generation downward and accepts the first that
        // verifies — here, 3 → 2 → 1 succeeds.
        Assert.AreEqual(1u, JournalRecordCodec.VerifyByDescent(signedBytes, signature, hierarchy, maxGeneration: 3));

        // A forged signature verifies at no generation.
        signature[0] ^= 0x01;
        Assert.IsNull(JournalRecordCodec.VerifyByDescent(signedBytes, signature, hierarchy, maxGeneration: 3));
    }

    [TestMethod]
    [DataRow(3UL, 50_000UL, false)]  // neither condition holds
    [DataRow(9UL, 50_000UL, false)]  // generation alone — a slow writer must not expire on siblings' activity
    [DataRow(3UL, 120_000UL, false)] // duration alone — wall clock alone reintroduces the clock dependency
    [DataRow(9UL, 120_000UL, true)]  // both — expired
    public void Expiry_requires_both_conditions(ulong currentGeneration, ulong nowMs, bool expected)
    {
        // Intent: expiry_generation 5, declared 100 000 ms, issued at 0,
        // skew margin 10 000 ms → duration door opens at 110 000 ms.
        Assert.AreEqual(expected, IntentLifecycle.IsExpired(
            expiryGeneration: 5,
            declaredMaxDurationMs: 100_000,
            issuedAtMs: 0,
            currentGeneration,
            nowMs,
            skewMarginMs: 10_000));
    }

    [TestMethod]
    public void The_skew_margin_delays_the_duration_door()
    {
        // Duration elapsed exactly, but the skew margin has not — not expired.
        Assert.IsFalse(IntentLifecycle.IsExpired(5, 100_000, 0, 9, 100_000, 10_000));
        Assert.IsTrue(IntentLifecycle.IsExpired(5, 100_000, 0, 9, 110_000, 10_000));
    }

    [TestMethod]
    public void The_survey_unions_extensions_honours_retirement_and_treats_unparseable_as_live()
    {
        var records = new List<JournalRecord>
        {
            new(JournalRecordKind.WriteIntent, Writer, 1, 0,
                new JournalPayload.WriteIntent(BackupSet, [Blob(1)], 100_000, 99, IntentPurpose.Backup)),
            new(JournalRecordKind.IntentExtension, Writer, 2, 10,
                new JournalPayload.IntentExtension(1, [Blob(2)], 100_000)),
            new(JournalRecordKind.WriteIntent, Writer, 3, 0,
                new JournalPayload.WriteIntent(BackupSet, [Blob(9)], 100_000, 99, IntentPurpose.Backup)),
            new(JournalRecordKind.IntentRetirement, Writer, 4, 20,
                new JournalPayload.IntentRetirement(3, IntentOutcome.Completed)),
        };

        var survey = IntentSurveyor.Survey(records, unparseableCount: 0, currentGeneration: 0, nowMs: 0, skewMarginMs: 0);

        // Intent 1 is live and covers its own blobs plus the extension's;
        // intent 3 was retired — an event, not a heartbeat (08 §5).
        var live = Assert.ContainsSingle(survey.LiveIntents);
        Assert.AreEqual(1UL, live.Sequence);
        Assert.IsTrue(survey.IsCovered(Blob(1)));
        Assert.IsTrue(survey.IsCovered(Blob(2)));
        Assert.IsFalse(survey.IsCovered(Blob(9)));

        // An unparseable intent means the collector is older than the writer
        // or the record is damaged — both call for the conservative reading:
        // everything is reachable (08 §8).
        var conservative = IntentSurveyor.Survey(records, unparseableCount: 1, 0, 0, 0);
        Assert.IsTrue(conservative.IsCovered(Blob(9)));
        Assert.IsTrue(conservative.IsCovered(Blob(0xEE)));
    }
}
