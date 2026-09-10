namespace CodeSpace.Messages.Budget;

/// <summary>
/// WHICH cap a budget admission was refused by. Before P15-5b-ii there was only one grain, so a refusal reason
/// could say "the cap" and mean the run's; now that a reservation is admitted against the run cap AND the team's
/// (or, absent a team row, the deployment's), a reader who cannot tell them apart cannot tell "this run asked for
/// too much" from "this team has spent its month" — two situations with opposite remedies (raise this launch's cap
/// versus wait, or raise the team's).
/// </summary>
public enum BudgetCapGrain
{
    /// <summary>The per-launch cap carried by the reservation itself (<c>RouteCaps.MaxCostUsd</c>).</summary>
    Run,

    /// <summary>The team's own standing cap over a rolling window — a <c>budget_team_cap</c> row.</summary>
    Team,

    /// <summary>The deployment-wide fallback applied to a team with no cap row of its own.</summary>
    Deployment,
}
