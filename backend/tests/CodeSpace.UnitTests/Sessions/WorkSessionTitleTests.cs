using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins the pure title-derivation contract of <see cref="WorkSessionService.SanitizeTitle"/> — the safety net that
/// makes any free-text goal a valid one-line <c>work_session.title</c>:
///   collapse every whitespace run (newlines / tabs / repeats) to a single space + trim, fall back to a default
///   when nothing is left, and never exceed <see cref="WorkSession.TitleMaxLength"/> (the column width).
/// Also hard-pins <see cref="WorkSession.TitleMaxLength"/> so a drift from migration 0069's <c>VARCHAR(256)</c> is
/// a test-visible decision (Rule 8): the service truncates to THIS value, so widening the column without widening
/// the const would silently keep clipping titles, and narrowing the column without the const would crash inserts.
/// </summary>
[Trait("Category", "Unit")]
public class WorkSessionTitleTests
{
    [Theory]
    [InlineData("Fix the auth refactor", "Fix the auth refactor")]               // already clean — unchanged
    [InlineData("  padded title  ", "padded title")]                              // trimmed
    [InlineData("line one\nline two", "line one line two")]                       // newline collapsed to a space
    [InlineData("tabs\tand\tspaces", "tabs and spaces")]                          // tabs collapsed
    [InlineData("multiple     spaces", "multiple spaces")]                        // repeated spaces collapsed
    [InlineData("\n\n  Trim me \t", "Trim me")]                                   // mixed leading/trailing whitespace
    [InlineData("", "Untitled session")]                                          // empty → fallback
    [InlineData("   \n\t  ", "Untitled session")]                                 // whitespace-only → fallback
    public void SanitizeTitle_collapses_whitespace_and_falls_back(string raw, string expected) =>
        WorkSessionService.SanitizeTitle(raw).ShouldBe(expected);

    [Fact]
    public void SanitizeTitle_at_exactly_the_max_length_is_unchanged()
    {
        var exact = new string('x', WorkSession.TitleMaxLength);

        WorkSessionService.SanitizeTitle(exact).ShouldBe(exact, "a title at exactly the column width must not be truncated");
    }

    [Fact]
    public void SanitizeTitle_over_the_max_length_is_truncated_with_an_ellipsis_and_never_overflows()
    {
        var result = WorkSessionService.SanitizeTitle(new string('x', WorkSession.TitleMaxLength + 200));

        result.Length.ShouldBe(WorkSession.TitleMaxLength, "an over-long title MUST be clipped to the column width so the insert can never overflow");
        result.ShouldEndWith("…", customMessage: "the clip is signalled with an ellipsis so the truncation is visible");
    }

    [Fact]
    public void SanitizeTitle_never_splits_an_astral_surrogate_pair_at_the_boundary()
    {
        // An emoji (😀 = a surrogate PAIR) placed so its HIGH surrogate lands at index (max-2) — exactly where the
        // naive cut [..(max-1)] would slice the pair, leaving a lone high surrogate. The clip must back off a char
        // rather than emit one — every char in the result must be a valid (paired) UTF-16 unit.
        var emoji = "😀";
        var overLong = new string('x', WorkSession.TitleMaxLength - 2) + emoji + new string('y', 10);

        var result = WorkSessionService.SanitizeTitle(overLong);

        result.Length.ShouldBeLessThanOrEqualTo(WorkSession.TitleMaxLength, "the result never overflows the column even after backing off a surrogate");
        result.ShouldNotContain(c => char.IsHighSurrogate(c) || char.IsLowSurrogate(c),
            customMessage: "no LONE surrogate may survive the clip — the boundary backed off the whole pair (the emoji was dropped)");
        result.ShouldEndWith("…");
    }

    [Fact]
    public void TitleMaxLength_is_pinned_to_the_migration_column_width()
    {
        // Migration 0069 declares title VARCHAR(256). SanitizeTitle truncates to this const + the EF config maps
        // HasMaxLength(this). All three must agree; this pins the C# side so the SQL literal has a guarded partner.
        WorkSession.TitleMaxLength.ShouldBe(256);
    }
}
