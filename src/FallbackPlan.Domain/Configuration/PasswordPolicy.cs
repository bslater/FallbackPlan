namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// One unmet account-password rule. Returned as a value rather than a
/// sentence so each surface renders it — the console as a live checklist,
/// the service refusal as prose — and so a test asserts the rule instead of
/// its wording.
/// </summary>
public enum PasswordFinding
{
    /// <summary>Below <see cref="PasswordPolicy.MinimumLength"/>.</summary>
    TooShort,

    /// <summary>No uppercase letter.</summary>
    NoUppercase,

    /// <summary>Fewer than two digits.</summary>
    FewerThanTwoDigits,

    /// <summary>No special character — nothing that is neither letter nor digit.</summary>
    NoSpecialCharacter,
}

/// <summary>What <see cref="PasswordPolicy.Assess"/> concluded.</summary>
/// <param name="Findings">Every unmet rule, in a stable order; empty when all are met.</param>
public sealed record PasswordAssessment(IReadOnlyList<PasswordFinding> Findings)
{
    /// <summary>Whether the password satisfies every rule.</summary>
    public bool IsAcceptable => Findings.Count == 0;
}

/// <summary>
/// The account-password policy (FR-USR-001 as amended, ADR-0045): a floor of
/// ten text elements plus the passphrase's composition rules — an uppercase
/// letter, two digits, a special character. No score and no bands, because a
/// password form wants a checklist, not a meter; and unlike the passphrase a
/// password CAN be changed, so the floor sits lower than the passphrase's
/// sixteen.
/// </summary>
/// <remarks>
/// Applied where a password is <b>chosen</b> — account creation and password
/// change — and never where one is presented: a login is verified against
/// the stored hash whatever rules were in force when the password was set,
/// so tightening this policy strands nobody outside their account.
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>The enforced floor, in text elements.</summary>
    public const int MinimumLength = 10;

    /// <summary>
    /// Assesses a candidate password against every rule at once, so a form
    /// renders the whole checklist rather than revealing one rule per
    /// attempt.
    /// </summary>
    /// <param name="candidate">The text as typed, before normalisation.</param>
    /// <returns>Every unmet rule; empty when the password passes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    public static PasswordAssessment Assess(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<PasswordFinding>();

        if (PassphraseStrength.TextElementCount(candidate) < MinimumLength)
        {
            findings.Add(PasswordFinding.TooShort);
        }

        var composition = PassphraseStrength.ScanComposition(candidate);
        if (!composition.HasUppercase)
        {
            findings.Add(PasswordFinding.NoUppercase);
        }

        if (composition.DigitCount < PassphraseStrength.MinimumDigits)
        {
            findings.Add(PasswordFinding.FewerThanTwoDigits);
        }

        if (!composition.HasSpecial)
        {
            findings.Add(PasswordFinding.NoSpecialCharacter);
        }

        return new PasswordAssessment(findings);
    }
}
