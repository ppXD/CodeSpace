using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="OutputCap"/> — the reusable result-shedding primitive. It must bound large output to a
/// head+tail preview WITHOUT ever growing the value, be deterministic (byte-stable across durable replay), and
/// pass small / no-cap / null inputs through untouched.
/// </summary>
[Trait("Category", "Unit")]
public class OutputCapTests
{
    [Fact]
    public void Within_budget_is_returned_verbatim()
    {
        var r = OutputCap.Apply("hello world", 100);

        r.Text.ShouldBe("hello world");
        r.Truncated.ShouldBeFalse();
        r.OriginalLength.ShouldBe(11);
    }

    [Fact]
    public void Exactly_at_budget_is_not_truncated()
    {
        var text = new string('x', 50);

        var r = OutputCap.Apply(text, 50);

        r.Truncated.ShouldBeFalse();
        r.Text.ShouldBe(text);
    }

    [Fact]
    public void Over_budget_keeps_head_and_tail_with_an_omitted_marker_and_never_grows()
    {
        var text = new string('A', 5000) + "MIDDLE" + new string('Z', 5000);

        var r = OutputCap.Apply(text, 200);

        r.Truncated.ShouldBeTrue();
        r.OriginalLength.ShouldBe(text.Length, "the original size is always reported, even when capped");
        r.Text.Length.ShouldBeLessThan(text.Length, "a cap must shrink, never grow, the value");
        r.Text.ShouldStartWith("A", customMessage: "the head is kept");
        r.Text.ShouldEndWith("Z", customMessage: "the tail is kept");
        r.Text.ShouldContain("omitted", customMessage: "a marker names how much was dropped");
        r.Text.ShouldContain(text.Length.ToString(), customMessage: "the marker reports the original length");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Non_positive_budget_means_no_cap(int maxChars)
    {
        var text = new string('x', 10_000);

        var r = OutputCap.Apply(text, maxChars);

        r.Truncated.ShouldBeFalse();
        r.Text.ShouldBe(text);
        r.OriginalLength.ShouldBe(10_000);
    }

    [Fact]
    public void Null_text_becomes_empty()
    {
        var r = OutputCap.Apply(null, 100);

        r.Text.ShouldBe("");
        r.Truncated.ShouldBeFalse();
        r.OriginalLength.ShouldBe(0);
    }

    [Fact]
    public void A_small_overflow_the_marker_would_grow_is_left_verbatim()
    {
        // 11 chars capped at 10: a head+tail preview plus the ~30-char marker would be LONGER than the original,
        // so capping is pointless — return it verbatim rather than bloating it.
        var text = new string('x', 11);

        var r = OutputCap.Apply(text, 10);

        r.Truncated.ShouldBeFalse();
        r.Text.ShouldBe(text);
    }

    [Fact]
    public void A_tiny_budget_keeps_a_head_only_preview_without_throwing()
    {
        var text = new string('A', 1000);

        var r = OutputCap.Apply(text, 1);

        r.Truncated.ShouldBeTrue();
        r.Text.ShouldStartWith("A");
        r.Text.Length.ShouldBeLessThan(text.Length);
    }

    [Fact]
    public void Is_deterministic_so_a_capped_value_is_stable_across_replay()
    {
        var text = new string('A', 9000) + new string('Z', 9000);

        OutputCap.Apply(text, 256).Text.ShouldBe(OutputCap.Apply(text, 256).Text);
    }
}
