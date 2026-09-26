namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// How a candidate passphrase measures against the policy that governs
/// <b>choosing</b> one (ADR-0044 §6, answering open question Q14).
/// </summary>
/// <remarks>
/// <para>
/// Four bands, of which two pass. The floor exists because a passphrase that
/// can never be changed is the worst possible place to accept four
/// characters; the estimate above the floor exists because length alone
/// cannot tell <c>aaaaaaaaaaaa</c> from a real one.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not zxcvbn. It consults no dictionary, no
/// word list, and no breach corpus, so it cannot distinguish a strong
/// passphrase from a famous quotation of the same shape —
/// <c>Password-12345678</c> satisfies every stated rule and passes, and
/// saying so plainly is more useful than implying otherwise. It is a floor
/// that catches the obviously bad, and calling it a floor is the honest
/// description of what it does.
/// </para>
/// </remarks>
public enum PassphraseStrengthBand
{
    /// <summary>Shorter than <see cref="PassphraseStrength.MinimumLength"/>. Refused.</summary>
    TooShort,

    /// <summary>Long enough, but structurally poor. Refused.</summary>
    Weak,

    /// <summary>Acceptable. Passes.</summary>
    Fair,

    /// <summary>Comfortably beyond the bar. Passes.</summary>
    Strong,
}

/// <summary>
/// One structural observation about a candidate. Returned as a value rather
/// than a sentence so a caller renders it — the console live, the CLI on
/// stderr — and so a test asserts the observation instead of its wording.
/// </summary>
public enum PassphraseFinding
{
    /// <summary>Below the minimum length.</summary>
    TooShort,

    /// <summary>Every character is the same one.</summary>
    SingleRepeatedCharacter,

    /// <summary>A short unit repeated to fill the length — <c>abcabcabcabc</c>.</summary>
    ShortRepeatedCycle,

    /// <summary>Only one character class, at a length that does not carry it alone.</summary>
    OneCharacterClassOnly,

    /// <summary>Long, but built from very few distinct characters.</summary>
    FewDistinctCharacters,

    /// <summary>Long enough that a single character class no longer costs score — the composition rules still apply.</summary>
    LengthCarriesIt,

    /// <summary>Three or more character classes are present.</summary>
    VariedCharacterClasses,

    /// <summary>No uppercase letter. The composition rules require one.</summary>
    NoUppercase,

    /// <summary>Fewer than two digits. The composition rules require two.</summary>
    FewerThanTwoDigits,

    /// <summary>No special character — nothing that is neither letter nor digit. The composition rules require one.</summary>
    NoSpecialCharacter,
}

/// <summary>What <see cref="PassphraseStrength.Assess"/> concluded.</summary>
/// <param name="Band">The band, and so whether setup may proceed.</param>
/// <param name="Score">The raw score, 0–100, for a meter to render.</param>
/// <param name="Findings">
/// What was observed, positive and negative, in a stable order.
/// </param>
public sealed record PassphraseAssessment(
    PassphraseStrengthBand Band, int Score, IReadOnlyList<PassphraseFinding> Findings)
{
    /// <summary>Whether a passphrase in this band may be accepted at setup.</summary>
    public bool IsAcceptable => Band is PassphraseStrengthBand.Fair or PassphraseStrengthBand.Strong;
}

/// <summary>
/// The passphrase policy, applied where a passphrase is <b>chosen</b> and
/// deliberately nowhere else (ADR-0044 §6).
/// </summary>
/// <remarks>
/// <para>
/// This does not live in <c>Passphrase.Create</c>, and that placement is the
/// decision rather than an accident of layering. <c>Create</c> is on the
/// restore path as well as the creation path: a policy tightened there would
/// refuse to open repositories that were made legitimately under the older
/// rule, turning a hardening change into data loss. A rule applied at the
/// moment of choosing costs nothing and can be raised freely; the same rule
/// applied at the moment of using locks people out of their own backups.
/// </para>
/// <para>
/// Every comparison here is ordinal and every classification is by Unicode
/// category, so the same candidate assesses identically under every culture.
/// </para>
/// </remarks>
public static class PassphraseStrength
{
    /// <summary>
    /// The enforced floor, and the single source for it — specification 03
    /// §2.1 asks for a minimum and names no number; ADR-0044 §6 as amended
    /// names this one.
    /// </summary>
    public const int MinimumLength = 16;

    /// <summary>The composition rules require at least this many digits.</summary>
    public const int MinimumDigits = 2;

    /// <summary>At or above this score a candidate is <see cref="PassphraseStrengthBand.Fair"/>.</summary>
    private const int FairScore = 40;

    /// <summary>At or above this score a candidate is <see cref="PassphraseStrengthBand.Strong"/>.</summary>
    private const int StrongScore = 60;

    /// <summary>Beyond this length, one character class is no longer held against a candidate.</summary>
    private const int LengthThatCarriesOneClass = 20;

    /// <summary>Length beyond this adds nothing further to the score.</summary>
    private const int LengthScoreCeiling = 40;

    /// <summary>
    /// Assesses a candidate passphrase. Never throws for any input except
    /// <see langword="null"/> — it is called on every keystroke of a
    /// half-typed secret, and a strength meter that can crash is a setup
    /// screen that can strand somebody mid-ceremony.
    /// </summary>
    /// <param name="candidate">The text as typed, before normalisation.</param>
    /// <returns>The band, a 0–100 score, and what was observed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    public static PassphraseAssessment Assess(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<PassphraseFinding>();

        // Counted in text elements rather than UTF-16 units: an emoji or a
        // combining sequence is one character to the person who typed it,
        // and charging them two for it — or one for a surrogate half —
        // would make the floor mean something different per script.
        var length = TextElementCount(candidate);

        if (length < MinimumLength)
        {
            return new PassphraseAssessment(
                PassphraseStrengthBand.TooShort, 0, [PassphraseFinding.TooShort]);
        }

        var distinct = DistinctCharacterCount(candidate);
        var classes = CharacterClassCount(candidate);

        if (distinct == 1)
        {
            // No arithmetic can rescue this, so none is attempted: a hundred
            // of the same character is one character's worth of secret.
            return new PassphraseAssessment(
                PassphraseStrengthBand.Weak, 0, [PassphraseFinding.SingleRepeatedCharacter]);
        }

        var score = Math.Min(length, LengthScoreCeiling) * 2;

        score += classes switch
        {
            >= 4 => 18,
            3 => 14,
            2 => 8,
            _ => 0,
        };

        score += Math.Min(distinct, 20);

        if (classes >= 3)
        {
            findings.Add(PassphraseFinding.VariedCharacterClasses);
        }

        if (classes == 1)
        {
            if (length >= LengthThatCarriesOneClass)
            {
                // A long passphrase of ordinary lowercase words is a good
                // passphrase. Demanding a digit of it would push people
                // towards short-and-decorated, which is worse.
                findings.Add(PassphraseFinding.LengthCarriesIt);
            }
            else
            {
                findings.Add(PassphraseFinding.OneCharacterClassOnly);
                score -= 15;
            }
        }

        if (distinct * 4 <= length)
        {
            findings.Add(PassphraseFinding.FewDistinctCharacters);
            score -= 10;
        }

        if (HasShortRepeatedCycle(candidate))
        {
            findings.Add(PassphraseFinding.ShortRepeatedCycle);
            score -= 25;
        }

        score = Math.Clamp(score, 0, 100);

        var band = score >= StrongScore
            ? PassphraseStrengthBand.Strong
            : score >= FairScore
                ? PassphraseStrengthBand.Fair
                : PassphraseStrengthBand.Weak;

        // The composition rules (ADR-0044 §6 as amended): an uppercase
        // letter, two digits, a special character. A miss caps the band at
        // Weak whatever the score — the meter must never show a passing bar
        // over a refusal — and every unmet rule is named at once, so a
        // person fixes the checklist in one pass.
        var composition = ScanComposition(candidate);
        if (!composition.HasUppercase)
        {
            findings.Add(PassphraseFinding.NoUppercase);
        }

        if (composition.DigitCount < MinimumDigits)
        {
            findings.Add(PassphraseFinding.FewerThanTwoDigits);
        }

        if (!composition.HasSpecial)
        {
            findings.Add(PassphraseFinding.NoSpecialCharacter);
        }

        if (!composition.Satisfied && band > PassphraseStrengthBand.Weak)
        {
            band = PassphraseStrengthBand.Weak;
        }

        return new PassphraseAssessment(band, score, findings);
    }

    /// <summary>The composition facts both policies read (this one and <see cref="PasswordPolicy"/>).</summary>
    /// <param name="HasUppercase">Whether any rune is uppercase.</param>
    /// <param name="DigitCount">How many runes are digits.</param>
    /// <param name="HasSpecial">Whether any rune is neither letter nor digit.</param>
    internal readonly record struct CompositionFacts(bool HasUppercase, int DigitCount, bool HasSpecial)
    {
        /// <summary>Whether every composition rule is met.</summary>
        public bool Satisfied => HasUppercase && DigitCount >= MinimumDigits && HasSpecial;
    }

    /// <summary>
    /// One pass over the runes for the composition rules. Classified by
    /// Unicode category, like everything here, so the verdict is the same
    /// under every culture; "special" means neither letter nor digit, which
    /// deliberately includes spaces — a separator is exactly the kind of
    /// character the rule wants present.
    /// </summary>
    internal static CompositionFacts ScanComposition(string value)
    {
        var upper = false;
        var digits = 0;
        var special = false;

        foreach (var rune in value.EnumerateRunes())
        {
            if (System.Text.Rune.IsUpper(rune))
            {
                upper = true;
            }

            if (System.Text.Rune.IsDigit(rune))
            {
                digits++;
            }
            else if (!System.Text.Rune.IsLetter(rune))
            {
                special = true;
            }
        }

        return new CompositionFacts(upper, digits, special);
    }

    /// <summary>
    /// The length as a reader would count it: one per grapheme cluster, so a
    /// combining sequence or an astral character counts once. Shared with
    /// <see cref="PasswordPolicy"/> so the two floors cannot disagree about
    /// what "a character" is.
    /// </summary>
    internal static int TextElementCount(string value)
    {
        var count = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>How many distinct code points appear, compared ordinally.</summary>
    private static int DistinctCharacterCount(string value)
    {
        var seen = new HashSet<int>();
        foreach (var rune in value.EnumerateRunes())
        {
            seen.Add(rune.Value);
        }

        return seen.Count;
    }

    /// <summary>
    /// How many of the four classes — lower, upper, digit, everything else —
    /// are present. Classified by Unicode category, so a Cyrillic lowercase
    /// letter counts as lowercase exactly as a Latin one does.
    /// </summary>
    private static int CharacterClassCount(string value)
    {
        bool lower = false, upper = false, digit = false, other = false;

        foreach (var rune in value.EnumerateRunes())
        {
            if (System.Text.Rune.IsLower(rune))
            {
                lower = true;
            }
            else if (System.Text.Rune.IsUpper(rune))
            {
                upper = true;
            }
            else if (System.Text.Rune.IsDigit(rune))
            {
                digit = true;
            }
            else
            {
                other = true;
            }
        }

        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
    }

    /// <summary>
    /// Whether the candidate is a short unit repeated to fill its length —
    /// <c>abcabcabcabc</c> carries three characters of secret however long
    /// it is made.
    /// </summary>
    /// <remarks>
    /// The smallest period is found directly rather than by a string search,
    /// because a candidate is short and the direct form is the one a reader
    /// can check. A period is "short" when the unit repeats at least three
    /// times; twice is a doubled word, which is weak-ish but not the same
    /// thing.
    /// </remarks>
    private static bool HasShortRepeatedCycle(string value)
    {
        var span = value.AsSpan();

        for (var period = 1; period * 3 <= span.Length; period++)
        {
            if (span.Length % period != 0)
            {
                continue;
            }

            var repeats = true;
            for (var index = period; index < span.Length && repeats; index++)
            {
                repeats = span[index] == span[index - period];
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }
}
