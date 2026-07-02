using CodeSpace.Messages.Enums;

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

    /// <summary>Per-repo (repositoryId → branch/ref) clone overrides — session branch continuity: a follow-up turn starts each repo from the prior turn's produced branch for THAT repo (primary + each related). A repo ABSENT from the map clones at its default branch. Null / empty = a fresh launch (byte-identical — every repo default).</summary>
    public IReadOnlyDictionary<Guid, string>? BaseRefs { get; init; }

    /// <summary>The supervisor's OWN brain-model credentialed-row id (a <c>ModelCredentialModel</c> id), resolved at launch when the Deep lane projects an <c>agent.supervisor</c> node and the operator pinned none — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>supervisorModelId</c> so the decider has a brain instead of stopping turn-1. Resolved ONCE here (replay-stable: every turn + replay reads the same baked id). Null for a non-supervisor projection or an empty pool (the builder then emits no brain — the honest fail-closed floor).</summary>
    public Guid? SupervisorBrainModelId { get; init; }

    /// <summary>The operator's allowed model pool (credentialed-model ROW ids) for the agents a Deep run dispatches, validated TEAM-SCOPED at launch — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>allowedModelIds</c>, where a dispatched model out of the pool fails closed. Null / empty ⇒ the pool is all the team's models (the builder omits the key — byte-identical). Inert on a non-supervisor projection (its builder never reads this).</summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    /// <summary>The operator's allowed AGENT (persona) pool (<c>AgentDefinition</c> ROW ids), validated TEAM-SCOPED at launch — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>allowedAgentDefinitionIds</c>, where a dispatched persona out of the pool fails closed. Null / empty ⇒ all the team's personas (builder omits the key — byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<Guid>? AllowedAgentDefinitionIds { get; init; }

    /// <summary>The operator's free-text ACCEPTANCE CRITERIA — the <c>SupervisorDefinitionBuilder</c> bakes them into the node's <c>acceptanceCriteria</c>, rendered into the decider prompt (NOT executed). Null / empty ⇒ the builder omits the key (byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>The S3 plan-confirmation gate — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>requirePlanConfirmation</c>, parking every authored plan version for the operator before any agent is created. False (the default) ⇒ the builder omits the key (byte-identical). Inert on a non-supervisor projection.</summary>
    public bool RequirePlanConfirmation { get; init; }

    /// <summary>How an INDEPENDENT critic reviews each supervisor decision — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>decisionReviewMode</c>. <see cref="ReviewMode.None"/> (the default) ⇒ the builder omits the key (byte-identical). Inert on a non-supervisor projection.</summary>
    public ReviewMode DecisionReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The credentialed-model ROW the decision critic runs on — baked into the node's <c>reviewerModelId</c>. Null ⇒ omitted ⇒ the critic auto-picks the team brain. Only consulted when <see cref="DecisionReviewMode"/> is not None.</summary>
    public Guid? ReviewerModelId { get; init; }
}
