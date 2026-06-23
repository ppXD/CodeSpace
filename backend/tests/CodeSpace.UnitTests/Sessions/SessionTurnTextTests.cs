using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins the shared turn-text reader (<see cref="SessionTurnText"/>) — the ONE place both the digest builder and the
/// summarizer read a turn's clean fields, so the recent window + the distilled summary stay on the same source-of-truth
/// + projection-shape contract. The result fallback chain (single-agent <c>summary</c> / plan-map <c>combined</c> /
/// supervisor <c>reason</c>) + the malformed-JSON tolerance + the clip are pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class SessionTurnTextTests
{
    [Theory]
    [InlineData("""{"summary":"s"}""", "s")]                                   // single-agent
    [InlineData("""{"combined":"c"}""", "c")]                                  // plan-map
    [InlineData("""{"reason":"r"}""", "r")]                                    // supervisor
    [InlineData("""{"summary":"s","combined":"c","reason":"r"}""", "s")]       // first present wins (summary)
    [InlineData("""{"combined":"c","reason":"r"}""", "c")]                     // then combined
    [InlineData("""{"branch":"b"}""", null)]                                   // none of the result keys → null
    public void ReadResult_follows_the_projection_fallback_chain(string outputsJson, string? expected) =>
        SessionTurnText.ReadResult(outputsJson).ShouldBe(expected);

    [Theory]
    [InlineData("""{"goal":"do it"}""", "goal", "do it")]
    [InlineData("""{"goal":""}""", "goal", null)]              // blank → null
    [InlineData("""{"goal":"   "}""", "goal", null)]           // whitespace → null
    [InlineData("""{"goal":42}""", "goal", null)]              // non-string → null
    [InlineData("""{}""", "goal", null)]                       // absent → null
    [InlineData("not json", "goal", null)]                     // malformed → null (no throw)
    [InlineData("[1,2,3]", "goal", null)]                      // non-object root → null
    public void ReadString_is_tolerant(string json, string field, string? expected) =>
        SessionTurnText.ReadString(json, field).ShouldBe(expected);

    [Fact]
    public void Clip_truncates_at_the_cap_with_an_ellipsis()
    {
        var huge = new string('Z', SessionTurnText.MaxResultChars + 100);

        var clipped = SessionTurnText.Clip(huge);

        clipped.Length.ShouldBe(SessionTurnText.MaxResultChars + 1, "clipped to the cap plus the ellipsis char");
        clipped.ShouldEndWith("…");
    }

    [Fact]
    public void Clip_leaves_a_short_string_untouched() =>
        SessionTurnText.Clip("short").ShouldBe("short");
}
