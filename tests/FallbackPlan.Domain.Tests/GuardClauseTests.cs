using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The refusals: every identifier rejects a wrong-sized buffer rather than
/// reading past it or silently truncating, and the validated configuration
/// types name each defect they find. A guard that is never exercised is a
/// guard nobody knows works.
/// </summary>
public sealed class GuardClauseTests
{
    private static byte[] Bytes(int size) => [.. Enumerable.Range(0, size).Select(index => (byte)index)];

    /// <summary>
    /// Each identifier's fixed width, paired with the two operations that
    /// take a caller-supplied buffer. Listed as data rather than as a test
    /// each, so a new identifier is one row and cannot be forgotten in a way
    /// that looks complete.
    /// </summary>
    public static TheoryData<string, int, Action<byte[]>, Action<byte[]>> BufferOperations => new()
    {
        { "ObjectId", ObjectId.Size, bytes => ObjectId.FromBytes(bytes), destination => ObjectId.FromBytes(Bytes(ObjectId.Size)).CopyTo(destination) },
        { "ContentId", ContentId.Size, bytes => ContentId.FromBytes(bytes), destination => ContentId.FromBytes(Bytes(ContentId.Size)).CopyTo(destination) },
        { "BlobId", BlobId.Size, bytes => BlobId.FromBytes(bytes), destination => BlobId.FromBytes(Bytes(BlobId.Size)).CopyTo(destination) },
        { "KeyId", KeyId.Size, bytes => KeyId.FromBytes(bytes), destination => KeyId.FromBytes(Bytes(KeyId.Size)).CopyTo(destination) },
        { "RepositoryId", RepositoryId.Size, bytes => RepositoryId.FromBytes(bytes), destination => RepositoryId.FromBytes(Bytes(RepositoryId.Size)).CopyTo(destination) },
        { "WriterId", WriterId.Size, bytes => WriterId.FromBytes(bytes), destination => WriterId.FromBytes(Bytes(WriterId.Size)).CopyTo(destination) },
        { "StoreBlobKey", StoreBlobKey.Size, bytes => StoreBlobKey.FromBytes(bytes), destination => StoreBlobKey.FromBytes(Bytes(StoreBlobKey.Size)).CopyTo(destination) },
        { "CheckpointId", CheckpointId.Size, bytes => CheckpointId.FromBytes(bytes), destination => CheckpointId.FromBytes(Bytes(CheckpointId.Size)).CopyTo(destination) },
        { "DeltaId", DeltaId.Size, bytes => DeltaId.FromBytes(bytes), destination => DeltaId.FromBytes(Bytes(DeltaId.Size)).CopyTo(destination) },
    };

    [Theory]
    [MemberData(nameof(BufferOperations))]
    public void An_identifier_refuses_a_wrong_sized_buffer(
        string name, int size, Action<byte[]> fromBytes, Action<byte[]> copyTo)
    {
        // One byte short and one byte long are both refused: a length check
        // written as "less than" would accept the long buffer and quietly
        // ignore the tail.
        Assert.Throws<ArgumentException>(() => fromBytes(Bytes(size - 1)));
        Assert.Throws<ArgumentException>(() => fromBytes(Bytes(size + 1)));
        Assert.Throws<ArgumentException>(() => fromBytes([]));

        // CopyTo must refuse a destination it would overrun. A longer one is
        // fine — the caller may be writing into a larger frame.
        Assert.Throws<ArgumentException>(() => copyTo(new byte[size - 1]));
        copyTo(new byte[size]);
        copyTo(new byte[size + 8]);

        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void A_round_trip_through_bytes_preserves_every_identifier()
    {
        Assert.Equal(Bytes(ObjectId.Size), ObjectId.FromBytes(Bytes(ObjectId.Size)).ToArray());
        Assert.Equal(Bytes(ContentId.Size), ContentId.FromBytes(Bytes(ContentId.Size)).ToArray());
        Assert.Equal(Bytes(BlobId.Size), BlobId.FromBytes(Bytes(BlobId.Size)).ToArray());
        Assert.Equal(Bytes(KeyId.Size), KeyId.FromBytes(Bytes(KeyId.Size)).ToArray());
        Assert.Equal(Bytes(RepositoryId.Size), RepositoryId.FromBytes(Bytes(RepositoryId.Size)).ToArray());
        Assert.Equal(Bytes(WriterId.Size), WriterId.FromBytes(Bytes(WriterId.Size)).ToArray());
        Assert.Equal(Bytes(StoreBlobKey.Size), StoreBlobKey.FromBytes(Bytes(StoreBlobKey.Size)).ToArray());
        Assert.Equal(Bytes(CheckpointId.Size), CheckpointId.FromBytes(Bytes(CheckpointId.Size)).ToArray());
        Assert.Equal(Bytes(DeltaId.Size), DeltaId.FromBytes(Bytes(DeltaId.Size)).ToArray());
    }

    [Fact]
    public void A_fresh_random_identifier_differs_from_the_last()
    {
        // Not a randomness test — a wiring test. A NewRandom that returned a
        // constant would reuse a checkpoint or delta identity across
        // publications, which the index's precedence rules cannot survive.
        Assert.NotEqual(CheckpointId.NewRandom(), CheckpointId.NewRandom());
        Assert.NotEqual(DeltaId.NewRandom(), DeltaId.NewRandom());
    }

    [Fact]
    public void A_content_identifier_never_renders_itself()
    {
        // NFR-SEC-004: a content identifier in a log is a confirmation
        // oracle for file contents, so ToString redacts rather than renders.
        var content = ContentId.FromBytes(Bytes(ContentId.Size));

        Assert.DoesNotContain("00", content.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", content.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ configuration

    [Fact]
    public void Argon2_parallelism_of_zero_is_named_as_its_own_defect()
    {
        // Zero is not merely "below the minimum": it is not computable in
        // any mode, so it is reported as a distinct defect rather than
        // folded into the minimum check.
        var result = (Argon2Parameters.CreationMinimums with { Parallelism = 0 }).ValidateCreationMinimums();

        Assert.False(result.IsValid);
        Assert.Contains(result.Defects, defect => defect.Name == "kdf_parallelism_zero");
    }

    [Fact]
    public void A_non_positive_open_blob_age_is_refused()
    {
        var result = (BlobWriteProfile.LocalDefault with { OpenBlobMaximumAge = TimeSpan.Zero }).Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Defects, defect => defect.Name == "blob_open_age_not_positive");

        var negative = (BlobWriteProfile.LocalDefault with { OpenBlobMaximumAge = TimeSpan.FromSeconds(-1) }).Validate();
        Assert.Contains(negative.Defects, defect => defect.Name == "blob_open_age_not_positive");
    }

    // ----------------------------------------------------------- segments

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_segment_size_that_is_not_positive_is_refused(int bytes)
    {
        Assert.False(SegmentSize.TryCreate(bytes, out var size));
        Assert.Equal(default, size);
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentSize.Create(bytes));
    }

    [Theory]
    // Specification 09 §3.1: the target is a power of two in 64 KiB – 16 MiB,
    // min ≥ target/8, max ≤ target×8, min ≤ max. Each row breaks exactly one
    // clause, so a weakened check cannot pass by satisfying a different one.
    [InlineData(1_000_000, 262_144, 8_388_608)]      // target not a power of two
    [InlineData(4_096, 512, 32_768)]                 // target below the 64 KiB floor
    [InlineData(33_554_432, 4_194_304, 67_108_864)]  // target above the 16 MiB ceiling
    [InlineData(1_048_576, 65_536, 8_388_608)]       // min below target/8
    [InlineData(1_048_576, 262_144, 16_777_216)]     // max above target×8
    [InlineData(1_048_576, 4_194_304, 524_288)]      // min above max
    [InlineData(0, 0, 0)]
    public void Cdc_parameters_outside_the_specified_ranges_are_refused(int target, int minimum, int maximum)
    {
        Assert.False(CdcParameters.TryCreate(target, minimum, maximum, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdcParameters.Create(target, minimum, maximum));
    }

    [Fact]
    public void Cdc_parameters_at_the_boundaries_are_accepted()
    {
        // The edges are the interesting cases: an off-by-one in any bound
        // shows up here rather than in the middle of the range.
        Assert.True(CdcParameters.TryCreate(1_048_576, 131_072, 8_388_608, out var exact));
        Assert.Equal(1_048_576, exact.TargetSize);
        Assert.Equal(131_072, exact.MinSize);
        Assert.Equal(8_388_608, exact.MaxSize);

        Assert.True(CdcParameters.TryCreate(65_536, 8_192, 524_288, out _));       // the floor
        Assert.True(CdcParameters.TryCreate(16_777_216, 2_097_152, 134_217_728, out _)); // the ceiling
    }

    // ---------------------------------------------------------- path rules

    [Fact]
    public void A_compiled_rule_keeps_its_text_verbatim()
    {
        // The policy manifest records the rule as the user wrote it, so a
        // compiled rule must carry its own source rather than a normalised
        // rendering — otherwise a published manifest cannot be compared with
        // the configuration it came from.
        Assert.True(PathRule.TryCreate("docs/**/*.txt", caseSensitive: true, out var rule, out var defect));
        Assert.Null(defect);
        Assert.NotNull(rule);
        Assert.Equal("docs/**/*.txt", rule!.Text);
        Assert.True(rule.Matches("docs/a/b/notes.txt"));
        Assert.False(rule.Matches("docs/a/b/notes.md"));
    }

    [Theory]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("double//slash")]
    [InlineData("/")]
    public void A_rule_with_an_empty_component_is_refused_by_name(string rule)
    {
        // rules-v1 (ADR-0024, 06 §7.1): an empty component is ambiguous
        // between "the root" and "a name that is nothing", so it is refused
        // at compile time rather than silently matching everything.
        Assert.False(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect));
        Assert.Null(compiled);
        Assert.NotNull(defect);
        Assert.Contains("empty component", defect!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_empty_rule_is_refused()
    {
        Assert.False(PathRule.TryCreate(string.Empty, caseSensitive: true, out _, out var defect));
        Assert.Contains("empty rule", defect!, StringComparison.Ordinal);
    }

    // ------------------------------------------------- the re: rule form

    [Theory]
    [InlineData("re:", "empty rule")]
    [InlineData("re:/leading", "empty component")]
    [InlineData("re:trailing/", "empty component")]
    [InlineData("re:double//slash", "empty component")]
    [InlineData("re:^anchored", "anchors are outside")]
    [InlineData("re:(?i)inline", "(?...) constructs")]
    [InlineData("re:back\\1", "backslash-alphanumeric")]
    [InlineData("re:trailing\\", "trailing backslash")]
    [InlineData("re:brace{", "counted quantifier")]
    public void A_regex_rule_outside_the_rules_v1_subset_is_refused_by_name(string rule, string expected)
    {
        // rules-v1 admits a raw regex behind an `re:` prefix, but only a
        // subset of it: no anchors (rules are implicitly anchored), no
        // inline constructs, no shorthand classes or backreferences. Each
        // refusal names the clause, because a rule a user cannot fix is a
        // rule they will work around.
        Assert.False(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect));
        Assert.Null(compiled);
        Assert.NotNull(defect);
        Assert.Contains(expected, defect!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("re:docs/[a-z]*[.]txt", "docs/notes.txt", "docs/NOTES.txt")]
    [InlineData("re:logs/[0-9]{4}", "logs/2026", "logs/26")]
    public void A_regex_rule_inside_the_subset_compiles_and_matches(string rule, string matching, string notMatching)
    {
        Assert.True(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect), defect);
        Assert.Null(defect);
        Assert.True(compiled!.Matches(matching), $"'{rule}' did not match '{matching}'");
        Assert.False(compiled.Matches(notMatching), $"'{rule}' matched '{notMatching}'");

        // Implicitly anchored: a rule matches the whole path or nothing.
        Assert.False(compiled.Matches("prefix/" + matching));
        Assert.False(compiled.Matches(matching + "/suffix"));
    }

    [Fact]
    public void A_regex_rule_that_passes_the_subset_but_will_not_compile_is_refused()
    {
        // The subset check is a syntactic screen, not a parse: a counted
        // quantifier with an inverted range satisfies it and still fails
        // Regex construction. The catch exists for exactly this, and a
        // guard that has never fired is a guard nobody knows works.
        Assert.False(PathRule.TryCreate("re:docs/a{9,2}", caseSensitive: true, out var compiled, out var defect));
        Assert.Null(compiled);
        Assert.Contains("does not compile", defect!, StringComparison.Ordinal);
    }
}
