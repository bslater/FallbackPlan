using FallbackPlan.Domain.Configuration;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// The policy that governs choosing a passphrase (FR-SVC-011, ADR-0044 §6,
/// answering open question Q14): a floor that refuses the too-short, an
/// estimate that refuses the structurally poor, and an honest ceiling on what
/// it claims to know.
/// </summary>
[TestClass]
public sealed class PassphraseStrengthTests
{
    [TestMethod]
    public void Assess_Null_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => PassphraseStrength.Assess(null!));

    [TestMethod]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("hunter2")]
    [DataRow("Tr0ub4dor&3")]                 // 11 — one short of the floor
    public void Assess_ShorterThanTheFloor_IsTooShortAndSaysOnlyThat(string candidate)
    {
        var assessment = PassphraseStrength.Assess(candidate);

        Assert.AreEqual(PassphraseStrengthBand.TooShort, assessment.Band);
        Assert.IsFalse(assessment.IsAcceptable);
        Assert.ContainsSingle(assessment.Findings);
        Assert.AreEqual(PassphraseFinding.TooShort, assessment.Findings[0]);
    }

    [TestMethod]
    public void Assess_ExactlyTheFloor_IsNoLongerTooShort()
    {
        // The boundary is worth pinning in both directions: a floor that is
        // off by one refuses a passphrase the documentation promised.
        Assert.AreEqual(12, PassphraseStrength.MinimumLength);
        Assert.AreNotEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('x', PassphraseStrength.MinimumLength - 1) + "Q9!").Band);
        Assert.AreEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('x', PassphraseStrength.MinimumLength - 1)).Band);
    }

    [TestMethod]
    [DataRow("aaaaaaaaaaaa")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Assess_OneCharacterRepeated_IsWeakHoweverLong(string candidate)
    {
        // Length cannot rescue this: a hundred of the same character is one
        // character's worth of secret.
        var assessment = PassphraseStrength.Assess(candidate);

        Assert.AreEqual(PassphraseStrengthBand.Weak, assessment.Band);
        Assert.AreEqual(0, assessment.Score);
        Assert.Contains(PassphraseFinding.SingleRepeatedCharacter, assessment.Findings);
    }

    [TestMethod]
    [DataRow("abcabcabcabc")]
    [DataRow("abababababababab")]
    [DataRow("xyzwxyzwxyzwxyzw")]
    public void Assess_AShortUnitRepeated_IsWeakAndNamesTheCycle(string candidate)
    {
        var assessment = PassphraseStrength.Assess(candidate);

        Assert.AreEqual(PassphraseStrengthBand.Weak, assessment.Band);
        Assert.IsFalse(assessment.IsAcceptable);
        Assert.Contains(PassphraseFinding.ShortRepeatedCycle, assessment.Findings);
    }

    [TestMethod]
    public void Assess_AUnitRepeatedOnlyTwice_IsNotTreatedAsACycle()
    {
        // Twice is a doubled word — weak-ish, and not the same thing as a
        // three-character unit stretched to fill a length requirement.
        Assert.DoesNotContain(
            PassphraseFinding.ShortRepeatedCycle,
            PassphraseStrength.Assess("greenhousegreenhouse").Findings);
    }

    [TestMethod]
    public void Assess_ShortAndOneClassOnly_IsPenalisedForIt()
    {
        var assessment = PassphraseStrength.Assess("abcdefghijkl");

        Assert.Contains(PassphraseFinding.OneCharacterClassOnly, assessment.Findings);
        Assert.DoesNotContain(PassphraseFinding.LengthCarriesIt, assessment.Findings);
    }

    [TestMethod]
    public void Assess_LongAndOneClassOnly_IsNotPenalisedForIt()
    {
        // A long passphrase of ordinary lowercase words is a good passphrase.
        // Demanding a digit of it pushes people towards short-and-decorated,
        // which is worse — so this case must pass, and pass without the
        // one-class finding.
        var assessment = PassphraseStrength.Assess("correcthorsebatterystaple");

        Assert.IsTrue(
            assessment.IsAcceptable,
            $"a 25-character passphrase assessed {assessment.Band} at score {assessment.Score}");
        Assert.Contains(PassphraseFinding.LengthCarriesIt, assessment.Findings);
        Assert.DoesNotContain(PassphraseFinding.OneCharacterClassOnly, assessment.Findings);
    }

    [TestMethod]
    public void Assess_ThreeOrMoreClasses_SaysSo()
    {
        var assessment = PassphraseStrength.Assess("Vault-Door-19-Kestrel");

        Assert.Contains(PassphraseFinding.VariedCharacterClasses, assessment.Findings);
        Assert.AreEqual(PassphraseStrengthBand.Strong, assessment.Band);
    }

    [TestMethod]
    public void Assess_LongButVeryFewDistinctCharacters_SaysSo()
    {
        var assessment = PassphraseStrength.Assess("abbaabbaabbaabbaabbaabba");

        Assert.Contains(PassphraseFinding.FewDistinctCharacters, assessment.Findings);
        Assert.IsFalse(assessment.IsAcceptable);
    }

    [TestMethod]
    public void Assess_AFamousWeakChoiceOfTheRightShape_Passes()
    {
        // Not a bug — the documented ceiling. This assessment consults no
        // dictionary, so `Password1234` is twelve characters across three
        // classes and it cannot see anything else. ADR-0044 §6 states the
        // limitation rather than implying a reach the code does not have,
        // and this test exists so that claim stays true rather than becoming
        // a surprise the first time somebody checks.
        Assert.IsTrue(PassphraseStrength.Assess("Password1234").IsAcceptable);
    }

    [TestMethod]
    public void Assess_ScoresAndBands_AgreeWithEachOther()
    {
        foreach (var candidate in new[]
                 {
                     "short", "aaaaaaaaaaaa", "abcabcabcabc", "abcdefghijkl",
                     "correcthorsebatterystaple", "Vault-Door-19-Kestrel", "Password1234",
                 })
        {
            var assessment = PassphraseStrength.Assess(candidate);

            Assert.IsTrue(assessment.Score is >= 0 and <= 100, $"'{candidate}' scored {assessment.Score}");
            Assert.AreEqual(
                assessment.Band is PassphraseStrengthBand.Fair or PassphraseStrengthBand.Strong,
                assessment.IsAcceptable,
                $"'{candidate}' is {assessment.Band} but IsAcceptable is {assessment.IsAcceptable}");
        }
    }

    [TestMethod]
    public void Assess_UnderATurkishCulture_ReachesTheSameVerdict()
    {
        // The dotted/dotless I is where culture-sensitive character handling
        // goes wrong, and a passphrase that assesses differently by locale is
        // a passphrase that is accepted on one machine and refused on the
        // next — for a secret that can never be changed.
        const string Candidate = "Istanbul-Ismir-19";

        var neutral = PassphraseStrength.Assess(Candidate);

        using (new CultureScope("tr-TR"))
        {
            var turkish = PassphraseStrength.Assess(Candidate);

            Assert.AreEqual(neutral.Band, turkish.Band);
            Assert.AreEqual(neutral.Score, turkish.Score);
            SequenceAssert.AreEqual(neutral.Findings, turkish.Findings);
        }
    }

    [TestMethod]
    public void Assess_AnAstralCharacter_CountsOnceTowardsTheFloor()
    {
        // Counted as a reader counts it. Charging two for an emoji — or one
        // for half a surrogate pair — would make the floor mean a different
        // thing depending on the script somebody writes in.
        var withEmoji = new string('q', 10) + "\U0001F600\U0001F600";  // 12 elements, 14 UTF-16 units

        Assert.AreNotEqual(PassphraseStrengthBand.TooShort, PassphraseStrength.Assess(withEmoji).Band);
        Assert.AreEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('q', 9) + "\U0001F600").Band);
    }

    [TestMethod]
    public void Assess_AnyInput_NeverThrows() => PropertyCheck.Holds(this, maxTest: 500);

    private static bool Assess_AnyInput_NeverThrowsProperty(string? candidate)
    {
        // It runs on every keystroke of a half-typed secret. A strength meter
        // that can crash is a setup screen that can strand somebody in the
        // middle of the one ceremony they cannot repeat.
        if (candidate is null)
        {
            return true;
        }

        var assessment = PassphraseStrength.Assess(candidate);
        return assessment.Score is >= 0 and <= 100 && assessment.Findings is not null;
    }
}
