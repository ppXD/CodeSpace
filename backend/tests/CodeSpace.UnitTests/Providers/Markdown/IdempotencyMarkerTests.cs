using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Providers.Markdown;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Markdown;

/// <summary>
/// The tag a create carries so its retry can find what an earlier attempt already made. The probe is only as
/// exact as the tag: one per call, carried by every attempt of that call, and matched by no other call.
/// </summary>
[Trait("Category", "Unit")]
public class IdempotencyMarkerTests
{
    [Fact]
    public void A_marker_is_an_html_comment_unique_to_its_call()
    {
        var first = IdempotencyMarker.New();
        var second = IdempotencyMarker.New();

        first.ShouldMatch("^<!-- codespace:idempotency:[0-9a-f]{32} -->$", "an HTML comment is dropped from the rendered markdown on GitHub and GitLab but kept in the raw body the API returns");
        second.ShouldNotBe(first);
    }

    [Theory]
    [InlineData("Looks good to me.")]
    [InlineData("Ends with a newline\n")]
    [InlineData("")]
    [InlineData(null)]
    public void Append_ends_the_body_with_exactly_one_marker(string? body)
    {
        var marker = IdempotencyMarker.New();

        var marked = IdempotencyMarker.Append(body, marker);

        marked.ShouldEndWith(marker);
        Regex.Matches(marked, "codespace:idempotency:").Count.ShouldBe(1);
        marked.ShouldStartWith(body ?? string.Empty, customMessage: "the caller's text is kept verbatim ahead of the marker");
    }

    [Fact]
    public void IsIn_matches_only_its_own_marker()
    {
        var mine = IdempotencyMarker.New();
        var theirs = IdempotencyMarker.New();
        var body = IdempotencyMarker.Append("same text, two concurrent creates", mine);

        IdempotencyMarker.IsIn(body, mine).ShouldBeTrue();
        IdempotencyMarker.IsIn(body, theirs).ShouldBeFalse("a concurrent create with the same text must never adopt this one's effect");
        IdempotencyMarker.IsIn("same text, two concurrent creates", mine).ShouldBeFalse("a body without the marker was not made by this call");
        IdempotencyMarker.IsIn(null, mine).ShouldBeFalse();
    }

    // ── Strip: what CodeSpace shows is what was written ──
    // The rule is textual and anchored at the end: exactly one trailing marker goes, with the line breaks Append put
    // before it, whatever markdown precedes it. A marker anywhere else is somebody's text and stays.

    [Theory]
    [InlineData("Looks good to me.")]
    [InlineData("Ends with a newline\n")]
    [InlineData("")]
    [InlineData(null)]
    public void Strip_gives_back_exactly_what_Append_was_given(string? body)
    {
        var marker = IdempotencyMarker.New();

        IdempotencyMarker.Strip(IdempotencyMarker.Append(body, marker)).ShouldBe(body ?? string.Empty);
    }

    [Fact]
    public void Strip_leaves_a_body_without_a_marker_alone() => IdempotencyMarker.Strip("Plain text, no tag.").ShouldBe("Plain text, no tag.");

    [Fact]
    public void Strip_turns_a_marker_only_body_into_an_empty_one() => IdempotencyMarker.Strip(IdempotencyMarker.New()).ShouldBe(string.Empty, "an issue opened without a body must reach the page's placeholder");

    [Fact]
    public void Strip_removes_only_the_one_trailing_marker()
    {
        var mine = IdempotencyMarker.New();
        var quoted = IdempotencyMarker.New();

        IdempotencyMarker.Strip($"See:\n```\n{quoted}\n```\n\n{mine}").ShouldBe($"See:\n```\n{quoted}\n```", "a marker quoted inside a closed code fence is text");
        IdempotencyMarker.Strip($"```\nan unclosed fence\n\n{mine}").ShouldBe("```\nan unclosed fence", "the tag Append added goes even where an unclosed fence would render it");
        IdempotencyMarker.Strip($"{mine}\n\nEdited later by hand.").ShouldBe($"{mine}\n\nEdited later by hand.", "text after a marker means it is no longer the trailing tag");
        IdempotencyMarker.Strip($"An older copy {quoted}\n\n{mine}").ShouldBe($"An older copy {quoted}", "exactly one: an older marker copied into the text stays");
    }

    [Fact]
    public void Strip_tolerates_the_line_breaks_a_web_edit_rewrites()
    {
        var marker = IdempotencyMarker.New();

        IdempotencyMarker.Strip($"Edited on the website\r\n\r\n{marker}\r\n").ShouldBe("Edited on the website");
    }

    [Fact]
    public void Strip_passes_null_through() => IdempotencyMarker.Strip(null).ShouldBeNull();
}
