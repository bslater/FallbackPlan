using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The refusals: every identifier rejects a wrong-sized buffer rather than
/// reading past it or silently truncating, and the validated configuration
/// types name each defect they find. A guard that is never exercised is a
/// guard nobody knows works.
/// </summary>
[TestClass]
public sealed class GuardClauseTests
{
    private static byte[] Bytes(int size) => [.. Enumerable.Range(0, size).Select(index => (byte)index)];

    /// <summary>
    /// Each identifier's fixed width, paired with the two operations that
    /// take a caller-supplied buffer. Listed as data rather than as a test
    /// each, so a new identifier is one row and cannot be forgotten in a way
    /// that looks complete.
    /// </summary>
    public static IEnumerable<object[]> BufferOperations =>
    [
        Row("ObjectId", ObjectId.Size, bytes => ObjectId.FromBytes(bytes), destination => ObjectId.FromBytes(Bytes(ObjectId.Size)).CopyTo(destination)),
        Row("ContentId", ContentId.Size, bytes => ContentId.FromBytes(bytes), destination => ContentId.FromBytes(Bytes(ContentId.Size)).CopyTo(destination)),
        Row("BlobId", BlobId.Size, bytes => BlobId.FromBytes(bytes), destination => BlobId.FromBytes(Bytes(BlobId.Size)).CopyTo(destination)),
        Row("KeyId", KeyId.Size, bytes => KeyId.FromBytes(bytes), destination => KeyId.FromBytes(Bytes(KeyId.Size)).CopyTo(destination)),
        Row("RepositoryId", RepositoryId.Size, bytes => RepositoryId.FromBytes(bytes), destination => RepositoryId.FromBytes(Bytes(RepositoryId.Size)).CopyTo(destination)),
        Row("WriterId", WriterId.Size, bytes => WriterId.FromBytes(bytes), destination => WriterId.FromBytes(Bytes(WriterId.Size)).CopyTo(destination)),
        Row("StoreBlobKey", StoreBlobKey.Size, bytes => StoreBlobKey.FromBytes(bytes), destination => StoreBlobKey.FromBytes(Bytes(StoreBlobKey.Size)).CopyTo(destination)),
        Row("CheckpointId", CheckpointId.Size, bytes => CheckpointId.FromBytes(bytes), destination => CheckpointId.FromBytes(Bytes(CheckpointId.Size)).CopyTo(destination)),
        Row("DeltaId", DeltaId.Size, bytes => DeltaId.FromBytes(bytes), destination => DeltaId.FromBytes(Bytes(DeltaId.Size)).CopyTo(destination)),
    ];

    /// <summary>
    /// One row of <see cref="BufferOperations"/>, with the column types
    /// spelled out.
    /// </summary>
    /// <remarks>
    /// A data row reaches the runner as <c>object[]</c>, and a lambda inside
    /// one has nothing to infer its type from. Naming the columns here gives
    /// each argument a target type and, as a side effect, says what the row
    /// means — which the anonymous braces this replaced never did.
    /// </remarks>
    private static object[] Row(string name, int size, Action<byte[]> fromBytes, Action<byte[]> copyTo) =>
        [name, size, fromBytes, copyTo];

    [TestMethod]
    [DynamicData(nameof(BufferOperations))]
    public void An_identifier_refuses_a_wrong_sized_buffer(
        string name, int size, Action<byte[]> fromBytes, Action<byte[]> copyTo)
    {
        // One byte short and one byte long are both refused: a length check
        // written as "less than" would accept the long buffer and quietly
        // ignore the tail.
        Assert.ThrowsExactly<ArgumentException>(() => fromBytes(Bytes(size - 1)));
        Assert.ThrowsExactly<ArgumentException>(() => fromBytes(Bytes(size + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => fromBytes([]));

        // CopyTo must refuse a destination it would overrun. A longer one is
        // fine — the caller may be writing into a larger frame.
        Assert.ThrowsExactly<ArgumentException>(() => copyTo(new byte[size - 1]));
        copyTo(new byte[size]);
        copyTo(new byte[size + 8]);

        Assert.IsFalse(string.IsNullOrEmpty(name));
    }

    [TestMethod]
    public void Identifiers_RoundTrippedThroughBytes_PreserveTheirValue()
    {
        SequenceAssert.AreEqual(Bytes(ObjectId.Size), ObjectId.FromBytes(Bytes(ObjectId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(ContentId.Size), ContentId.FromBytes(Bytes(ContentId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(BlobId.Size), BlobId.FromBytes(Bytes(BlobId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(KeyId.Size), KeyId.FromBytes(Bytes(KeyId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(RepositoryId.Size), RepositoryId.FromBytes(Bytes(RepositoryId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(WriterId.Size), WriterId.FromBytes(Bytes(WriterId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(StoreBlobKey.Size), StoreBlobKey.FromBytes(Bytes(StoreBlobKey.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(CheckpointId.Size), CheckpointId.FromBytes(Bytes(CheckpointId.Size)).ToArray());
        SequenceAssert.AreEqual(Bytes(DeltaId.Size), DeltaId.FromBytes(Bytes(DeltaId.Size)).ToArray());
    }

    [TestMethod]
    public void Identifiers_GeneratedTwice_DifferFromEachOther()
    {
        // Not a randomness test — a wiring test. A NewRandom that returned a
        // constant would reuse a checkpoint or delta identity across
        // publications, which the index's precedence rules cannot survive.
        Assert.AreNotEqual(CheckpointId.NewRandom(), CheckpointId.NewRandom());
        Assert.AreNotEqual(DeltaId.NewRandom(), DeltaId.NewRandom());
    }

    [TestMethod]
    public void ContentId_RenderedToString_RedactsItsValue()
    {
        // NFR-SEC-004: a content identifier in a log is a confirmation
        // oracle for file contents, so ToString redacts rather than renders.
        var content = ContentId.FromBytes(Bytes(ContentId.Size));

        Assert.DoesNotContain("00", content.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", content.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ configuration

    [TestMethod]
    public void Argon2Parameters_ParallelismIsZero_NamesItsOwnDefect()
    {
        // Zero is not merely "below the minimum": it is not computable in
        // any mode, so it is reported as a distinct defect rather than
        // folded into the minimum check.
        var result = (Argon2Parameters.CreationMinimums with { Parallelism = 0 }).ValidateCreationMinimums();

        Assert.IsFalse(result.IsValid);
        Assert.Contains(defect => defect.Name == "kdf_parallelism_zero", result.Defects);
    }

    [TestMethod]
    public void BlobWriteProfile_OpenBlobAgeIsNotPositive_IsRefusedByName()
    {
        var result = (BlobWriteProfile.LocalDefault with { OpenBlobMaximumAge = TimeSpan.Zero }).Validate();

        Assert.IsFalse(result.IsValid);
        Assert.Contains(defect => defect.Name == "blob_open_age_not_positive", result.Defects);

        var negative = (BlobWriteProfile.LocalDefault with { OpenBlobMaximumAge = TimeSpan.FromSeconds(-1) }).Validate();
        Assert.Contains(defect => defect.Name == "blob_open_age_not_positive", negative.Defects);
    }

    // ----------------------------------------------------------- segments

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void A_segment_size_that_is_not_positive_is_refused(int bytes)
    {
        Assert.IsFalse(SegmentSize.TryCreate(bytes, out var size));
        Assert.AreEqual(default, size);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SegmentSize.Create(bytes));
    }

    [TestMethod]
    // Specification 09 §3.1: the target is a power of two in 64 KiB – 16 MiB,
    // min ≥ target/8, max ≤ target×8, min ≤ max. Each row breaks exactly one
    // clause, so a weakened check cannot pass by satisfying a different one.
    [DataRow(1_000_000, 262_144, 8_388_608)]      // target not a power of two
    [DataRow(4_096, 512, 32_768)]                 // target below the 64 KiB floor
    [DataRow(33_554_432, 4_194_304, 67_108_864)]  // target above the 16 MiB ceiling
    [DataRow(1_048_576, 65_536, 8_388_608)]       // min below target/8
    [DataRow(1_048_576, 262_144, 16_777_216)]     // max above target×8
    [DataRow(1_048_576, 4_194_304, 524_288)]      // min above max
    [DataRow(0, 0, 0)]
    public void Cdc_parameters_outside_the_specified_ranges_are_refused(int target, int minimum, int maximum)
    {
        Assert.IsFalse(CdcParameters.TryCreate(target, minimum, maximum, out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CdcParameters.Create(target, minimum, maximum));
    }

    [TestMethod]
    public void CdcParameters_AtTheBoundaryValues_AreAccepted()
    {
        // The edges are the interesting cases: an off-by-one in any bound
        // shows up here rather than in the middle of the range.
        Assert.IsTrue(CdcParameters.TryCreate(1_048_576, 131_072, 8_388_608, out var exact));
        Assert.AreEqual(1_048_576, exact.TargetSize);
        Assert.AreEqual(131_072, exact.MinSize);
        Assert.AreEqual(8_388_608, exact.MaxSize);

        Assert.IsTrue(CdcParameters.TryCreate(65_536, 8_192, 524_288, out _));       // the floor
        Assert.IsTrue(CdcParameters.TryCreate(16_777_216, 2_097_152, 134_217_728, out _)); // the ceiling
    }

    // ---------------------------------------------------------- path rules

    [TestMethod]
    public void PathRule_Compiled_KeepsItsTextVerbatim()
    {
        // The policy manifest records the rule as the user wrote it, so a
        // compiled rule must carry its own source rather than a normalised
        // rendering — otherwise a published manifest cannot be compared with
        // the configuration it came from.
        Assert.IsTrue(PathRule.TryCreate("docs/**/*.txt", caseSensitive: true, out var rule, out var defect));
        Assert.IsNull(defect);
        Assert.IsNotNull(rule);
        Assert.AreEqual("docs/**/*.txt", rule!.Text);
        Assert.IsTrue(rule.Matches("docs/a/b/notes.txt"));
        Assert.IsFalse(rule.Matches("docs/a/b/notes.md"));
    }

    [TestMethod]
    [DataRow("/leading")]
    [DataRow("trailing/")]
    [DataRow("double//slash")]
    [DataRow("/")]
    public void A_rule_with_an_empty_component_is_refused_by_name(string rule)
    {
        // rules-v1 (ADR-0024, 06 §7.1): an empty component is ambiguous
        // between "the root" and "a name that is nothing", so it is refused
        // at compile time rather than silently matching everything.
        Assert.IsFalse(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect));
        Assert.IsNull(compiled);
        Assert.IsNotNull(defect);
        Assert.Contains("empty component", defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PathRule_RuleTextIsEmpty_IsRefused()
    {
        Assert.IsFalse(PathRule.TryCreate(string.Empty, caseSensitive: true, out _, out var defect));
        Assert.Contains("empty rule", defect!, StringComparison.Ordinal);
    }

    // ------------------------------------------------- the re: rule form

    [TestMethod]
    [DataRow("re:", "empty rule")]
    [DataRow("re:/leading", "empty component")]
    [DataRow("re:trailing/", "empty component")]
    [DataRow("re:double//slash", "empty component")]
    [DataRow("re:^anchored", "anchors are outside")]
    [DataRow("re:(?i)inline", "(?...) constructs")]
    [DataRow("re:back\\1", "backslash-alphanumeric")]
    [DataRow("re:trailing\\", "trailing backslash")]
    [DataRow("re:brace{", "counted quantifier")]
    public void A_regex_rule_outside_the_rules_v1_subset_is_refused_by_name(string rule, string expected)
    {
        // rules-v1 admits a raw regex behind an `re:` prefix, but only a
        // subset of it: no anchors (rules are implicitly anchored), no
        // inline constructs, no shorthand classes or backreferences. Each
        // refusal names the clause, because a rule a user cannot fix is a
        // rule they will work around.
        Assert.IsFalse(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect));
        Assert.IsNull(compiled);
        Assert.IsNotNull(defect);
        Assert.Contains(expected, defect!, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("re:docs/[a-z]*[.]txt", "docs/notes.txt", "docs/NOTES.txt")]
    [DataRow("re:logs/[0-9]{4}", "logs/2026", "logs/26")]
    public void A_regex_rule_inside_the_subset_compiles_and_matches(string rule, string matching, string notMatching)
    {
        Assert.IsTrue(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect), defect);
        Assert.IsNull(defect);
        Assert.IsTrue(compiled!.Matches(matching), $"'{rule}' did not match '{matching}'");
        Assert.IsFalse(compiled.Matches(notMatching), $"'{rule}' matched '{notMatching}'");

        // Implicitly anchored: a rule matches the whole path or nothing.
        Assert.IsFalse(compiled.Matches("prefix/" + matching));
        Assert.IsFalse(compiled.Matches(matching + "/suffix"));
    }

    [TestMethod]
    public void PathRule_RegexPassesTheSubsetButWillNotCompile_IsRefused()
    {
        // The subset check is a syntactic screen, not a parse: a counted
        // quantifier with an inverted range satisfies it and still fails
        // Regex construction. The catch exists for exactly this, and a
        // guard that has never fired is a guard nobody knows works.
        Assert.IsFalse(PathRule.TryCreate("re:docs/a{9,2}", caseSensitive: true, out var compiled, out var defect));
        Assert.IsNull(compiled);
        Assert.Contains("does not compile", defect!, StringComparison.Ordinal);
    }
}
