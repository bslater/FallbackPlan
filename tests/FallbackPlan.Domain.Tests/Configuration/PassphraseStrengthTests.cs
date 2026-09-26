using FallbackPlan.Domain.Configuration;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// The policy that governs choosing a passphrase (FR-SVC-011, ADR-0044 §6 as
/// amended, answering open question Q14): a floor of sixteen that refuses the
/// too-short, composition rules — an uppercase letter, two digits, a special
/// character — that refuse the structurally narrow, an estimate that refuses
/// the structurally poor, and an honest ceiling on what it claims to know.
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
    [DataRow("Tr0ub4dor&3")]                 // the famous 11
    [DataRow("Password1234")]                // 12 — passed the old floor, four short of this one
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
        Assert.AreEqual(16, PassphraseStrength.MinimumLength);
        Assert.AreNotEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('x', PassphraseStrength.MinimumLength - 4) + "Q92!").Band);
        Assert.AreEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('x', PassphraseStrength.MinimumLength - 1)).Band);
    }

    [TestMethod]
    [DataRow("aaaaaaaaaaaaaaaa")]
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
    [DataRow("abcdabcdabcdabcd")]
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
        var assessment = PassphraseStrength.Assess("abcdefghijklmnop");

        Assert.Contains(PassphraseFinding.OneCharacterClassOnly, assessment.Findings);
        Assert.DoesNotContain(PassphraseFinding.LengthCarriesIt, assessment.Findings);
    }

    // ------------------------------------------------ the composition rules

    [TestMethod]
    [DataRow("no upper passphrase 42!", PassphraseFinding.NoUppercase)]
    [DataRow("No Digits Here At All!!", PassphraseFinding.FewerThanTwoDigits)]
    [DataRow("Only One Digit 4 here!!", PassphraseFinding.FewerThanTwoDigits)]
    [DataRow("NoSpecialsAtAll1234Abc", PassphraseFinding.NoSpecialCharacter)]
    public void Assess_AMissingCompositionRule_RefusesNamingIt(
        string candidate, PassphraseFinding expected)
    {
        var assessment = PassphraseStrength.Assess(candidate);

        Assert.IsFalse(assessment.IsAcceptable, $"'{candidate}' must not pass");
        Assert.AreEqual(PassphraseStrengthBand.Weak, assessment.Band);
        Assert.Contains(expected, assessment.Findings);
    }

    [TestMethod]
    public void Assess_EveryMissingRule_IsNamedAtOnce()
    {
        // The checklist a person fixes in one pass, not a whack-a-mole: all
        // three composition findings on a candidate missing all three. (No
        // spaces — a space counts as a special character, deliberately.)
        var assessment = PassphraseStrength.Assess("justlowercaseletters");

        Assert.Contains(PassphraseFinding.NoUppercase, assessment.Findings);
        Assert.Contains(PassphraseFinding.FewerThanTwoDigits, assessment.Findings);
        Assert.Contains(PassphraseFinding.NoSpecialCharacter, assessment.Findings);
    }

    [TestMethod]
    public void Assess_ExactlyTwoDigits_SatisfiesTheDigitRule()
    {
        var assessment = PassphraseStrength.Assess("A boundary passphrase 42");

        Assert.DoesNotContain(PassphraseFinding.FewerThanTwoDigits, assessment.Findings);
        Assert.IsTrue(assessment.IsAcceptable, $"assessed {assessment.Band} at {assessment.Score}");
    }

    [TestMethod]
    public void Assess_LongLowercaseAlone_NoLongerCarriesItself()
    {
        // The old policy deliberately let a long all-lowercase passphrase
        // pass. The amended one (ADR-0044 §6) does not: however long,
        // composition is required, and the refusal names what is missing
        // rather than scoring it away.
        var assessment = PassphraseStrength.Assess("correcthorsebatterystaple");

        Assert.IsFalse(assessment.IsAcceptable);
        Assert.AreEqual(PassphraseStrengthBand.Weak, assessment.Band);
        Assert.Contains(PassphraseFinding.NoUppercase, assessment.Findings);
        Assert.Contains(PassphraseFinding.FewerThanTwoDigits, assessment.Findings);
        Assert.Contains(PassphraseFinding.NoSpecialCharacter, assessment.Findings);
    }

    [TestMethod]
    public void Assess_ACompositionFailure_CapsTheBandWhateverTheScore()
    {
        // A candidate that would score Strong on length and variety alone
        // still reads Weak while a rule is unmet — the meter must not show
        // a green bar over a refusal.
        var assessment = PassphraseStrength.Assess("plenty of ordinary lowercase words 42 here");

        Assert.Contains(PassphraseFinding.NoUppercase, assessment.Findings);
        Assert.AreEqual(PassphraseStrengthBand.Weak, assessment.Band);
        Assert.IsFalse(assessment.IsAcceptable);
    }

    // -------------------------------------------------------- the estimator

    [TestMethod]
    public void Assess_ThreeOrMoreClasses_SaysSo()
    {
        var assessment = PassphraseStrength.Assess("Vault-Door-19-Kestrel");

        Assert.Contains(PassphraseFinding.VariedCharacterClasses, assessment.Findings);
        Assert.AreEqual(PassphraseStrengthBand.Strong, assessment.Band);
        Assert.IsTrue(assessment.IsAcceptable);
    }

    [TestMethod]
    public void Assess_LongButVeryFewDistinctCharacters_SaysSo()
    {
        var assessment = PassphraseStrength.Assess("abbaabbaabbaabbaabbaabba");

        Assert.Contains(PassphraseFinding.FewDistinctCharacters, assessment.Findings);
        Assert.IsFalse(assessment.IsAcceptable);
    }

    [TestMethod]
    public void Assess_AFamousWeakChoiceOfTheRightShape_StillPasses()
    {
        // Not a bug — the documented ceiling, restated for the amended
        // policy. This assessment consults no dictionary, so a candidate
        // that satisfies every stated rule passes however guessable a human
        // would find it. ADR-0044 §6 states the limitation rather than
        // implying a reach the code does not have; this test keeps that
        // claim true. (`Password1234` itself no longer passes — the floor
        // and the special-character rule catch it — but its longer,
        // decorated cousin sails through, and saying so plainly is more
        // useful than implying otherwise.)
        Assert.IsTrue(PassphraseStrength.Assess("Password-12345678").IsAcceptable);
    }

    [TestMethod]
    public void Assess_ScoresAndBands_AgreeWithEachOther()
    {
        foreach (var candidate in new[]
                 {
                     "short", "aaaaaaaaaaaaaaaa", "abcdabcdabcdabcd", "abcdefghijklmnop",
                     "correcthorsebatterystaple", "Vault-Door-19-Kestrel", "Password-12345678",
                     "no upper passphrase 42!",
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
        const string Candidate = "Istanbul-Ismir-1929";

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
        var withEmoji = new string('q', 14) + "\U0001F600\U0001F600";  // 16 elements, 18 UTF-16 units

        Assert.AreNotEqual(PassphraseStrengthBand.TooShort, PassphraseStrength.Assess(withEmoji).Band);
        Assert.AreEqual(
            PassphraseStrengthBand.TooShort,
            PassphraseStrength.Assess(new string('q', 14) + "\U0001F600").Band);
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
