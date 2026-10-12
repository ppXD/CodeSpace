using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A secret that meets a cut. Exact-match redaction recognises only a whole value, so text persisted in pieces (a stream
/// flushed by size or by time, a capture the runner stopped at its budget, a capped excerpt) must never split a secret
/// between two pieces, and a piece whose end was cut away must not keep the head of one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SecretBoundaryRedactionTests
{
    private const string Secret = "CANARY-boundary-0123456789abcdef";

    [Theory]
    [InlineData("nothing to see here", 19)]                   // no secret can start anywhere in it
    [InlineData("key=CANA", 4)]                                // the tail could still become the secret
    [InlineData("key=" + Secret, 36)]                          // a whole secret is settled: exact matching sees it
    [InlineData("key=" + Secret + " and C", 41)]               // only the trailing "C" is held back
    [InlineData("", 0)]
    public void SettledLength_holds_back_only_what_could_still_become_a_secret(string text, int expected)
    {
        new PersistenceSecretRedactor([Secret]).SettledLength(text).ShouldBe(expected);
    }

    [Fact]
    public void SettledLength_never_cuts_through_a_secret_that_overlaps_itself()
    {
        // "abXab" ends with its own first two characters. The tail "ab" alone would put the cut at 4 and release
        // "zabX" — three characters of the secret — so a whole occurrence across the cut moves it back to its start.
        new PersistenceSecretRedactor(["abXab"]).SettledLength("zabXab").ShouldBe(1);
    }

    [Fact]
    public void SettledLength_with_no_secrets_settles_everything()
    {
        new PersistenceSecretRedactor([]).SettledLength("anything at all").ShouldBe(15);
    }

    [Fact]
    public void Releasing_settled_pieces_leaves_no_fragment_of_a_secret_wherever_the_text_was_split()
    {
        var redactor = new PersistenceSecretRedactor([Secret]);
        const string text = "before " + Secret + " after";

        for (var split = 0; split <= text.Length; split++)
        {
            var arrived = text[..split];
            var settled = redactor.SettledLength(arrived);

            var persisted = redactor.Redact(arrived[..settled]).Value + redactor.Redact(arrived[settled..] + text[split..]).Value;

            persisted.ShouldBe("before [REDACTED] after", $"the text split at {split}");
        }
    }

    [Theory]
    [InlineData("deploy key=CANARY-boundary-012", "deploy key=[REDACTED]", true)]          // cut inside the secret
    [InlineData("ok " + Secret + " tail", "ok [REDACTED] tail", true)]                      // whole secret, clean end
    [InlineData("all clear", "all clear", false)]
    public void RedactCut_masks_a_trailing_fragment_that_could_be_the_start_of_a_secret(string text, string expected, bool changed)
    {
        var redaction = new PersistenceSecretRedactor([Secret]).RedactCut(text);

        redaction.Value.ShouldBe(expected);
        redaction.Changed.ShouldBe(changed);
    }

    [Fact]
    public void RedactCut_passes_null_through()
    {
        new PersistenceSecretRedactor([Secret]).RedactCut(null).Value.ShouldBeNull();
    }

    [Fact]
    public void Union_masks_every_secret_of_every_redactor_and_skips_absent_ones()
    {
        var union = PersistenceSecretRedactor.Union(new PersistenceSecretRedactor(["first-secret"]), null, new PersistenceSecretRedactor(["second-secret"]));

        union.Redact("first-secret then second-secret").Value.ShouldBe("[REDACTED] then [REDACTED]");
    }

    [Fact]
    public void None_masks_nothing()
    {
        PersistenceSecretRedactor.None.Redact("plain").Changed.ShouldBeFalse();
    }
}
