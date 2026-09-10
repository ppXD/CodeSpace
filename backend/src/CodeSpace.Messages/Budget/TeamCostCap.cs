namespace CodeSpace.Messages.Budget;

/// <summary>
/// A standing cost cap every reservation in the team is admitted against, over a rolling window. It is the answer to
/// the hole P15-5b-ii closes: the ledger's only sum was PER RUN, so a team could launch a thousand runs each under
/// its own modest cap and spend without any ceiling at all.
///
/// <para>Either a <c>budget_team_cap</c> row (<see cref="BudgetCapGrain.Team"/>) or, for a team with no row, the
/// deployment-wide fallback applied in its place (<see cref="BudgetCapGrain.Deployment"/>). The two are the same
/// arithmetic and differ only in <see cref="Grain"/>, which is what a refusal reason names so the remedy is legible.</para>
/// </summary>
public sealed record TeamCostCap(Guid TeamId, decimal CapUsd, string Window)
{
    /// <summary>
    /// The ONLY window in 5b-ii — the rolling 30 days ending now. Pinned by unit test: it is persisted in every cap
    /// row, so a rename would leave live rows carrying a window this build no longer recognises, and
    /// <see cref="WindowStart"/> deliberately THROWS on one rather than silently widening to no window at all.
    /// </summary>
    public const string RollingThirtyDays = "rolling-30d";

    /// <summary>The span <see cref="RollingThirtyDays"/> names. Pinned by unit test alongside the window's wire string.</summary>
    public static readonly TimeSpan RollingThirtyDaysSpan = TimeSpan.FromDays(30);

    /// <summary>Whether this cap came from the team's own row or from the deployment fallback standing in for one.</summary>
    public BudgetCapGrain Grain { get; init; } = BudgetCapGrain.Team;

    /// <summary>
    /// The instant the committed-sum window opens. An unrecognised window is a hard error, never "no window": a
    /// window this build cannot interpret would otherwise sum EVERY reservation the team ever made and refuse the
    /// team forever, or (had it defaulted the other way) sum none and admit everything.
    /// </summary>
    public DateTimeOffset WindowStart(DateTimeOffset now) => Window switch
    {
        RollingThirtyDays => now - RollingThirtyDaysSpan,
        _ => throw new ArgumentOutOfRangeException(nameof(Window), Window, $"unknown team cost cap window — the only window this build understands is '{RollingThirtyDays}'"),
    };
}
