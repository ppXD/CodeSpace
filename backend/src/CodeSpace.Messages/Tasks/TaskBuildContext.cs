namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The value object each <c>IWorkflowDefinitionBuilder</c> consumes to project a task into a
/// <c>WorkflowDefinition</c> (Rule 18.1, a pure data noun) — the normalized <see cref="Seed"/>, the chosen
/// <see cref="Route"/>, and the resolved <see cref="AgentProfile"/> the agent step(s) are stamped from. A
/// builder reads ONLY this context; it never touches the DB or the router, so it stays a pure function of its
/// input (Rule 16 — the logic that builds the context lives in the resolving services / router, not here).
///
/// <para>PR2-minimal: the spec's later <c>Bounds</c> / <c>Recipe</c> / <c>Plan</c> fields are intentionally
/// NOT modelled yet — the only bounds in play (<see cref="RouteCaps"/>) already ride on <see cref="Route"/>,
/// and the recipe/plan layers don't exist until a later PR. <see cref="GroundingContext"/> is surfaced
/// separately (and defaults to the seed's own grounding) so a builder can fold pre-gathered context into the
/// agent prompt without reaching into the seed; <see cref="AgentProfile"/> is nullable so a projection that
/// needs no agent step (a future non-agent recipe) can omit it.</para>
/// </summary>
public sealed record TaskBuildContext
{
    /// <summary>The normalized task seed (surface dimension already erased).</summary>
    public required TaskLaunchSeed Seed { get; init; }

    /// <summary>The routing decision — its <see cref="RoutePlan.ProjectionKind"/> selects the builder; its <see cref="RoutePlan.Caps"/> carry the bounds.</summary>
    public required RoutePlan Route { get; init; }

    /// <summary>The resolved agent envelope the agent step(s) are stamped from. Null when the projection emits no agent step.</summary>
    public ResolvedAgentProfile? AgentProfile { get; init; }

    /// <summary>The grounding context the projection may fold into the agent prompt. Null = none (a builder may fall back to <see cref="TaskLaunchSeed.GroundingContext"/>).</summary>
    public string? GroundingContext { get; init; }

    /// <summary>The branch/ref to clone the PRIMARY repo at — session branch continuity: a follow-up turn starts from the prior turn's produced branch. Null = the repo's default branch (a fresh launch, byte-identical).</summary>
    public string? PrimaryBaseRef { get; init; }
}
