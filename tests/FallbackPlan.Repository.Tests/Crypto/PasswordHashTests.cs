using System.Globalization;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The account password primitive (FR-USR-002, NFR-SEC-012; ADR-0045 §4): a
/// memory-hard one-way hash with its salt and parameters stored beside it.
/// </summary>
/// <remarks>
/// The cost parameters here are deliberately far below the creation minimums.
/// This suite is about the shape of the stored form and the decisions it
/// encodes, not about how expensive the derivation is — running every case at
/// 64 MiB and three iterations would add minutes to the suite and prove
/// nothing these assertions do not.
/// </remarks>
[TestClass]
public sealed class PasswordHashTests
{
    /// <summary>Cheap enough for a test to run many of, still a real derivation.</summary>
    private static readonly Argon2Parameters Fast = new()
    {
        MemoryKiB = 64,
        Iterations = 1,
        Parallelism = 1,
    };

    [TestMethod]
    public void Verify_TheSamePassword_Accepts()
    {
        var hash = PasswordHash.Create("correct horse battery staple", Fast);

        Assert.IsTrue(hash.Verify("correct horse battery staple"));
    }

    [TestMethod]
    public void Verify_ADifferentPassword_Refuses()
    {
        var hash = PasswordHash.Create("correct horse battery staple", Fast);

        Assert.IsFalse(hash.Verify("correct horse battery stapl"));
        Assert.IsFalse(hash.Verify("Correct horse battery staple"));
        Assert.IsFalse(hash.Verify(string.Empty));
    }

    [TestMethod]
    public void Create_TheSamePasswordTwice_ProducesDifferentStoredForms()
    {
        // The salt is per account, not per product. Two people who choose the
        // same password must not be visibly the same in the credential file,
        // and one cracked hash must not be every account holding that password.
        var first = PasswordHash.Create("shared password", Fast);
        var second = PasswordHash.Create("shared password", Fast);

        Assert.AreNotEqual(first.Encode(), second.Encode());
        Assert.IsTrue(first.Verify("shared password"));
        Assert.IsTrue(second.Verify("shared password"));
    }

    [TestMethod]
    public void Encode_ThenParse_VerifiesTheSamePassword()
    {
        var hash = PasswordHash.Create("round trip", Fast);

        Assert.IsTrue(PasswordHash.TryParse(hash.Encode(), out var parsed));
        Assert.IsTrue(parsed!.Verify("round trip"));
        Assert.IsFalse(parsed.Verify("round trips"));
    }

    [TestMethod]
    public void Encode_CarriesTheParametersItWasMadeWith()
    {
        // Raising the cost for new accounts must not lock out existing ones,
        // which is only possible because the cost is a fact stored with each
        // hash rather than a constant the verifier assumes.
        var cheap = PasswordHash.Create("aged account", Fast);
        var dearer = PasswordHash.Create(
            "new account", new Argon2Parameters { MemoryKiB = 128, Iterations = 2, Parallelism = 1 });

        Assert.IsTrue(PasswordHash.TryParse(cheap.Encode(), out var oldOne));
        Assert.IsTrue(PasswordHash.TryParse(dearer.Encode(), out var newOne));

        Assert.AreEqual(64u, oldOne!.Parameters.MemoryKiB);
        Assert.AreEqual(128u, newOne!.Parameters.MemoryKiB);
        Assert.IsTrue(oldOne.Verify("aged account"), "an account hashed under the old cost still opens");
    }

    [TestMethod]
    public void Create_AnEmptyPassword_IsRefused()
    {
        // Argon2id accepts a zero-length password, so refusing is this type's
        // job — the same division Passphrase draws.
        Assert.ThrowsExactly<ArgumentException>(() => PasswordHash.Create(string.Empty, Fast));
    }

    [TestMethod]
    public void TryParse_AnythingMalformed_AnswersFalseRatherThanThrowing()
    {
        // A tampered credential file is an authentication failure, not a crash:
        // whoever edited it must not be able to take the service down with it.
        var sound = PasswordHash.Create("intact", Fast).Encode();
        var fields = sound.Split('$');

        string[] malformed =
        [
            string.Empty,
            "not-a-hash",
            "fbp-argon2id$m=64,t=1,p=1",                                     // too few fields
            $"other-scheme${fields[1]}${fields[2]}${fields[3]}",             // unknown scheme
            $"{fields[0]}$m=64,t=1${fields[2]}${fields[3]}",                 // parameter missing
            $"{fields[0]}$m=x,t=1,p=1${fields[2]}${fields[3]}",              // parameter not a number
            $"{fields[0]}$m=64,t=1,p=0${fields[2]}${fields[3]}",             // zero parallelism
            $"{fields[0]}$m=64,t=1,p=999${fields[2]}${fields[3]}",           // parallelism past a byte
            $"{fields[0]}${fields[1]}$00${fields[3]}",                       // salt too short
            $"{fields[0]}${fields[1]}${fields[2]}$deadbeef",                 // tag too short
            $"{fields[0]}${fields[1]}$zz{fields[2][2..]}${fields[3]}",       // salt not hex
        ];

        foreach (var candidate in malformed)
        {
            Assert.IsFalse(
                PasswordHash.TryParse(candidate, out var parsed),
                $"'{candidate}' parsed when it should not have");
            Assert.IsNull(parsed);
        }

        Assert.IsFalse(PasswordHash.TryParse(null, out _));
    }

    [TestMethod]
    public void TryParse_AFlippedTagByte_NoLongerVerifies()
    {
        // The encoding is not authenticated and does not need to be — anyone
        // who can edit the file can replace the whole account — but a hash
        // that changed must stop accepting the password it was made from,
        // rather than accepting a neighbouring one.
        var hash = PasswordHash.Create("intact", Fast);
        var fields = hash.Encode().Split('$');
        var flipped = fields[3][0] == '0' ? '1' : '0';

        Assert.IsTrue(
            PasswordHash.TryParse($"{fields[0]}${fields[1]}${fields[2]}${flipped}{fields[3][1..]}", out var tampered));
        Assert.IsFalse(tampered!.Verify("intact"));
    }

    [TestMethod]
    public void ToString_IsRedacted_BecauseRedactionIsByDeclaredType()
    {
        // NFR-SEC-006: a hash is not the password, but it is what an attacker
        // takes away to become the password at leisure, so it never reaches a
        // log by being interpolated somewhere.
        var hash = PasswordHash.Create("never printed", Fast);
        var rendered = hash.ToString();

        Assert.AreEqual("password-hash(redacted)", rendered);
        Assert.DoesNotContain("never printed", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(hash.Encode(), rendered, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Verify_TheSamePasswordInTwoUnicodeCompositions_Accepts()
    {
        // The same characters typed on two operating systems can differ in
        // composition. An account that will not accept its own password on a
        // second machine is indistinguishable from a wrong password, which is
        // why this normalises exactly as a passphrase does.
        const string Composed = "café naïve";       // é and ï as single code points
        const string Decomposed = "café naïve";   // e + combining acute, i + diaeresis

        var hash = PasswordHash.Create(Composed, Fast);

        Assert.AreNotEqual(Composed, Decomposed, StringComparer.Ordinal);
        Assert.IsTrue(hash.Verify(Decomposed));
    }

    [TestMethod]
    public void Encode_UnderATurkishCulture_ParsesBack()
    {
        // CA1305 is an error in this tree because a formatter that follows the
        // ambient culture writes a stored credential one machine cannot read.
        // The Turkish dotless i is the classic way to find out.
        using (new CultureScope("tr-TR"))
        {
            var hash = PasswordHash.Create("kültür", Fast);
            var encoded = hash.Encode();

            Assert.IsTrue(PasswordHash.TryParse(encoded, out var parsed), encoded);
            Assert.IsTrue(parsed!.Verify("kültür"));
            Assert.StartsWith("fbp-argon2id$", encoded, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void Encode_ParsedUnderADifferentCulture_StillVerifies()
    {
        // The half the case above cannot prove on its own: a stored form
        // written on one machine has to read on another.
        string encoded;
        using (new CultureScope("tr-TR"))
        {
            encoded = PasswordHash.Create("portable", Fast).Encode();
        }

        using (new CultureScope("en-US"))
        {
            Assert.IsTrue(PasswordHash.TryParse(encoded, out var parsed));
            Assert.IsTrue(parsed!.Verify("portable"));
        }
    }

    [TestMethod]
    public void Encode_TheStoredForm_ContainsNothingResemblingThePassword()
    {
        var hash = PasswordHash.Create("Sup3rSecret!Phrase", Fast);
        var encoded = hash.Encode();

        Assert.DoesNotContain("Sup3rSecret", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(4, encoded.Split('$').Length, encoded);
        Assert.AreEqual(
            PasswordHash.SaltLength * 2, encoded.Split('$')[2].Length,
            "the salt is stored whole, so an account can be verified without any other state");
    }
}
