using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// The account-password policy (FR-USR-001 as amended, ADR-0045): a floor of
/// ten, plus the same composition the passphrase demands — an uppercase
/// letter, two digits, a special character. No score and no bands: a password
/// either satisfies every rule or the findings name the ones it misses, so a
/// form can render a checklist rather than a meter.
/// </summary>
[TestClass]
public sealed class PasswordPolicyTests
{
    [TestMethod]
    public void Assess_Null_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => PasswordPolicy.Assess(null!));

    [TestMethod]
    [DataRow("Owner-Pass-19!")]
    [DataRow("Aa12!Aa12!")]                 // exactly the floor of 10
    [DataRow("A very 42 ordinary one!")]    // spaces count as special characters
    public void Assess_EveryRuleMet_IsAcceptableWithNothingToSay(string candidate)
    {
        var assessment = PasswordPolicy.Assess(candidate);

        Assert.IsTrue(assessment.IsAcceptable, $"'{candidate}' was refused");
        Assert.IsEmpty(assessment.Findings);
    }

    [TestMethod]
    public void Assess_TheFloor_IsTenAndPinnedBothWays()
    {
        Assert.AreEqual(10, PasswordPolicy.MinimumLength);

        Assert.IsTrue(PasswordPolicy.Assess("Aa12!Aa12!").IsAcceptable);        // 10
        var nine = PasswordPolicy.Assess("Aa12!Aa12");                          // 9
        Assert.IsFalse(nine.IsAcceptable);
        Assert.Contains(PasswordFinding.TooShort, nine.Findings);
    }

    [TestMethod]
    [DataRow("no-upper-42!", PasswordFinding.NoUppercase)]
    [DataRow("No-Digits-Here!", PasswordFinding.FewerThanTwoDigits)]
    [DataRow("One-Digit-4!", PasswordFinding.FewerThanTwoDigits)]
    [DataRow("NoSpecials42x", PasswordFinding.NoSpecialCharacter)]
    public void Assess_AMissingRule_IsRefusedNamingIt(string candidate, PasswordFinding expected)
    {
        var assessment = PasswordPolicy.Assess(candidate);

        Assert.IsFalse(assessment.IsAcceptable, $"'{candidate}' must not pass");
        Assert.Contains(expected, assessment.Findings);
    }

    [TestMethod]
    public void Assess_EveryUnmetRule_IsNamedAtOnce()
    {
        // A checklist, not whack-a-mole: a short all-lowercase candidate
        // hears about all four rules in one answer.
        var assessment = PasswordPolicy.Assess("password");

        Assert.Contains(PasswordFinding.TooShort, assessment.Findings);
        Assert.Contains(PasswordFinding.NoUppercase, assessment.Findings);
        Assert.Contains(PasswordFinding.FewerThanTwoDigits, assessment.Findings);
        Assert.Contains(PasswordFinding.NoSpecialCharacter, assessment.Findings);
    }

    [TestMethod]
    public void Assess_AnAstralCharacter_CountsOnceTowardsTheFloor()
    {
        // The same text-element counting the passphrase floor uses, so the
        // two policies cannot disagree about what "a character" is. Nine
        // letters and an emoji is ten elements — and the emoji is a special
        // character.
        var assessment = PasswordPolicy.Assess("Aa12bcdef\U0001F600");

        Assert.DoesNotContain(PasswordFinding.TooShort, assessment.Findings);
        Assert.DoesNotContain(PasswordFinding.NoSpecialCharacter, assessment.Findings);
        Assert.IsTrue(assessment.IsAcceptable);
    }
}
