using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public class SecretRedactorTests
{
    [Fact]
    public void Masks_every_occurrence_of_a_secret()
    {
        var r = new SecretRedactor(new[] { "sk-secret" });

        r.Redact("key=sk-secret and again sk-secret").ShouldBe("key=*** and again ***");
    }

    [Fact]
    public void Fingerprint_is_null_when_empty_stable_per_key_and_never_the_key()
    {
        // The re-attach safety check (a fresh observer re-tails only if its rebuilt redactor's fingerprint
        // matches the one stamped at launch) relies on: empty → null; same key → same fingerprint (stable
        // across processes/restarts); different key → different; and it's a hash, never the key itself.
        SecretRedactor.None.Fingerprint.ShouldBeNull();
        new SecretRedactor(new[] { "   " }).Fingerprint.ShouldBeNull("blank secrets are dropped → empty → no fingerprint");

        var a = new SecretRedactor(new[] { "sk-aaa-key" }).Fingerprint;
        var aAgain = new SecretRedactor(new[] { "sk-aaa-key" }).Fingerprint;
        var b = new SecretRedactor(new[] { "sk-bbb-key" }).Fingerprint;

        a.ShouldNotBeNull();
        a.ShouldBe(aAgain, "same secret → same fingerprint, so a re-attach on a fresh backend can match it");
        a.ShouldNotBe(b, "a rotated/different key → a different fingerprint → re-attach refuses to re-tail");
        a!.ShouldNotContain("sk-aaa-key", customMessage: "the fingerprint is a one-way hash, never the key itself");
    }

    [Fact]
    public void Masks_a_secret_embedded_in_a_larger_blob()
    {
        var r = new SecretRedactor(new[] { "sk-secret" });

        r.Redact("{\"line\":\"init key=sk-secret done\"}").ShouldBe("{\"line\":\"init key=*** done\"}");
    }

    [Fact]
    public void Masks_the_longest_secret_first_so_a_contained_secret_does_not_leave_a_tail()
    {
        // "sk-abc" is a substring of "sk-abc-def"; masking the longer first leaves no "***-def" remnant.
        var r = new SecretRedactor(new[] { "sk-abc", "sk-abc-def" });

        r.Redact("token sk-abc-def here").ShouldBe("token *** here");
    }

    [Fact]
    public void None_is_the_identity()
    {
        SecretRedactor.None.IsEmpty.ShouldBeTrue();
        SecretRedactor.None.Redact("anything sk-secret").ShouldBe("anything sk-secret");
    }

    [Fact]
    public void Blank_and_whitespace_only_secrets_are_dropped_so_they_cannot_garble_output()
    {
        var r = new SecretRedactor(new[] { "", "   " });

        r.IsEmpty.ShouldBeTrue("a blank / whitespace-only value is never a real key — dropped so it can't mask spaces");
        r.Redact("untouched   spacing").ShouldBe("untouched   spacing");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Null_or_empty_text_is_returned_as_is(string? text) =>
        new SecretRedactor(new[] { "sk-secret" }).Redact(text!).ShouldBe(text);
}
