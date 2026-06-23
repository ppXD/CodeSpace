using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins the WIRE values of <see cref="WorkSessionKind"/> + <see cref="WorkSessionStatus"/>. Both are stored in
/// Postgres as the enum NAME via <c>HasConversion&lt;string&gt;</c> (the <c>work_session.kind</c> / <c>.status</c>
/// columns), so a rename — say <c>Pr</c> → <c>PullRequest</c> — silently orphans every existing row that holds the
/// old literal. Hard-pinning the strings makes such a rename a compile/test-visible decision (Rule 8's discipline),
/// and the count assertions force every NEW variant to land its own pinned case (Rule 9).
/// </summary>
[Trait("Category", "Unit")]
public class WorkSessionEnumTests
{
    [Theory]
    [InlineData(WorkSessionKind.Task, "Task")]
    [InlineData(WorkSessionKind.Pr, "Pr")]
    [InlineData(WorkSessionKind.Issue, "Issue")]
    [InlineData(WorkSessionKind.Workflow, "Workflow")]
    [InlineData(WorkSessionKind.Schedule, "Schedule")]
    [InlineData(WorkSessionKind.Custom, "Custom")]
    public void Kind_stored_value_is_pinned(WorkSessionKind kind, string stored) =>
        kind.ToString().ShouldBe(stored);

    [Theory]
    [InlineData(WorkSessionStatus.Open, "Open")]
    [InlineData(WorkSessionStatus.Archived, "Archived")]
    public void Status_stored_value_is_pinned(WorkSessionStatus status, string stored) =>
        status.ToString().ShouldBe(stored);

    [Fact]
    public void Kind_has_exactly_the_six_documented_variants()
    {
        // A new variant MUST add its own pinned InlineData above before this count moves.
        Enum.GetValues<WorkSessionKind>().Length.ShouldBe(6);
    }

    [Fact]
    public void Status_has_exactly_open_and_archived()
    {
        Enum.GetValues<WorkSessionStatus>().Length.ShouldBe(2);
    }
}
