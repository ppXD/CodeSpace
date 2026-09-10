using System;
using CodeSpace.Messages.Budget;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Budget;

/// <summary>
/// Pins the team cost cap's wire contract: the window string that is PERSISTED in every cap row, the span it means,
/// and the refusal wording each grain produces.
///
/// <para>Why pin them: the window is stored, so a rename leaves live rows carrying a name this build no longer
/// recognises — and the whole point of <see cref="TeamCostCap.WindowStart"/> throwing on one is that the alternative
/// (defaulting) silently means either "sum everything ever" or "sum nothing", i.e. refuse the team forever or admit
/// everything. The refusal words are operator-facing copy that names WHICH cap refused, which is the difference
/// between "raise this launch's cap" and "the team has spent its month".</para>
/// </summary>
[Trait("Category", "Unit")]
public class TeamCostCapContractTests
{
    [Fact]
    public void The_only_window_is_pinned()
    {
        TeamCostCap.RollingThirtyDays.ShouldBe("rolling-30d");
        TeamCostCap.RollingThirtyDaysSpan.ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public void A_cap_defaults_to_the_team_grain()
    {
        // The deployment fallback is the exception a resolver states explicitly; a cap read from a team's own row
        // must never have to remember to say so.
        new TeamCostCap(Guid.NewGuid(), 10m, TeamCostCap.RollingThirtyDays).Grain.ShouldBe(BudgetCapGrain.Team);
    }

    [Fact]
    public void The_window_opens_thirty_days_before_now()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var cap = new TeamCostCap(Guid.NewGuid(), 10m, TeamCostCap.RollingThirtyDays);

        cap.WindowStart(now).ShouldBe(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("rolling-7d")]
    [InlineData("")]
    [InlineData("Rolling-30d")]
    public void An_unrecognised_window_throws_rather_than_becoming_no_window(string window)
    {
        var cap = new TeamCostCap(Guid.NewGuid(), 10m, window);

        Should.Throw<ArgumentOutOfRangeException>(() => cap.WindowStart(DateTimeOffset.UtcNow))
            .Message.ShouldContain(TeamCostCap.RollingThirtyDays, customMessage: "the error must name the window this build DOES understand, so an operator can fix the row");
    }

    [Theory]
    [InlineData(BudgetCapGrain.Run, "run")]
    [InlineData(BudgetCapGrain.Team, "team")]
    [InlineData(BudgetCapGrain.Deployment, "deployment")]
    public void Every_grain_has_a_pinned_word(BudgetCapGrain grain, string expected) => BudgetCapRefusal.Word(grain).ShouldBe(expected);

    [Fact]
    public void An_unmapped_grain_throws_rather_than_printing_an_enum_name() =>
        Should.Throw<ArgumentOutOfRangeException>(() => BudgetCapRefusal.Word((BudgetCapGrain)99));

    [Fact]
    public void A_run_refusal_names_the_run_cap_and_omits_a_window() =>
        BudgetCapRefusal.Reason(BudgetCapGrain.Run, 12m, 10m).ShouldBe("admission would commit 12.0000 past the 10.0000 run cap");

    [Theory]
    [InlineData(BudgetCapGrain.Team, "team")]
    [InlineData(BudgetCapGrain.Deployment, "deployment")]
    public void A_windowed_refusal_names_the_grain_and_the_window(BudgetCapGrain grain, string word) =>
        BudgetCapRefusal.Reason(grain, 60.5m, 50m, TeamCostCap.RollingThirtyDays)
            .ShouldBe($"admission would commit 60.5000 past the 50.0000 {word} cap (rolling-30d)");
}
