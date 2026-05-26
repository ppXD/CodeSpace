using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.OAuth;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

public class PkceGeneratorTests
{
    [Fact]
    public void Generate_returns_43_char_base64url_verifier()
    {
        var pair = new PkceGenerator().Generate();

        // 32 bytes → 43 char base64url (256 / 6, rounded up, padding stripped)
        pair.Verifier.Length.ShouldBe(43);
        pair.Verifier.ShouldNotContain("=");
        pair.Verifier.ShouldNotContain("+");
        pair.Verifier.ShouldNotContain("/");
    }

    [Fact]
    public void Generate_challenge_is_sha256_of_verifier()
    {
        var pair = new PkceGenerator().Generate();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        pair.ChallengeS256.ShouldBe(expected);
    }

    [Fact]
    public void Successive_calls_produce_distinct_verifiers()
    {
        var gen = new PkceGenerator();
        var seen = Enumerable.Range(0, 100).Select(_ => gen.Generate().Verifier).ToHashSet();

        // Collision probability across 100 pulls of 256-bit randomness is astronomically low;
        // any failure here means RandomNumberGenerator is broken.
        seen.Count.ShouldBe(100);
    }

    [Fact]
    public void Verifier_uses_only_base64url_alphabet()
    {
        var verifier = new PkceGenerator().Generate().Verifier;

        var allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".ToHashSet();
        verifier.All(allowed.Contains).ShouldBeTrue();
    }
}
