using CodeSpace.Messages.Agents;
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

    /// <summary>True when the operator pinned a brain model but it was ineligible (missing / disabled / cross-team / non-structured) and <see cref="SupervisorBrainModelId"/> is the auto-selected FALLBACK, not the requested pin — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>brainModelPinIneligible</c> so the run's own definition records that the pin did not apply, instead of silently baking the fallback with no trace. False/null (no pin, or the pin was honored) ⇒ the builder omits the key (byte-identical).</summary>
    public bool SupervisorBrainModelPinIneligible { get; init; }

    /// <summary>The operator's allowed model pool (credentialed-model ROW ids) for the agents a Deep run dispatches, validated TEAM-SCOPED at launch — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>allowedModelIds</c>, where a dispatched model out of the pool fails closed. Null / empty ⇒ the pool is all the team's models (the builder omits the key — byte-identical). Inert on a non-supervisor projection (its builder never reads this).</summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    /// <summary>The operator's allowed AGENT (persona) pool (<c>AgentDefinition</c> ROW ids), validated TEAM-SCOPED at launch — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>allowedAgentDefinitionIds</c>, where a dispatched persona out of the pool fails closed. Null / empty ⇒ all the team's personas (builder omits the key — byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<Guid>? AllowedAgentDefinitionIds { get; init; }

    /// <summary>The operator's free-text ACCEPTANCE CRITERIA — the <c>SupervisorDefinitionBuilder</c> bakes them into the node's <c>acceptanceCriteria</c>, rendered into the decider prompt (NOT executed). Null / empty ⇒ the builder omits the key (byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>The plan-confirmation gate — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>requirePlanConfirmation</c> (S3); the plan-map builders insert a <c>plan.confirm</c> node after the planner and rebind the map to its APPROVED outputs (S4d). False (the default) ⇒ byte-identical ungated graphs. Inert on the quick tier.</summary>
    public bool RequirePlanConfirmation { get; init; }

    /// <summary>The session's chat surface (S4a) — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>conversationId</c>, giving the launched run's HITL cards (ask_human, plan confirmation, approvals) a real channel to post into. Null (single-agent / map launches, or a launch predating the surface) ⇒ the builder omits the key (byte-identical).</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The operator's pinned planner-model ROW (S4b) — validated at launch like the supervisor brain; the plan-map builders bake it into the plan.author node's <c>plannerModelId</c>. Null ⇒ omitted ⇒ the node auto-picks the team's strongest structured-eligible model. Inert on non-plan-map projections.</summary>
    public Guid? PlannerModelRowId { get; init; }

    /// <summary>How an INDEPENDENT critic reviews the AUTHORED PLAN — tier-generic (S4e): the plan-map builders bake it into plan.author/plan.confirm's <c>reviewMode</c>; the supervisor builder bakes it into the plan-scoped <c>planReviewMode</c>. <see cref="ReviewMode.None"/> (the default) ⇒ omitted (byte-identical). Inert on quick.</summary>
    public ReviewMode PlannerReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The operator's EXECUTABLE acceptance floor (S4b) — an argv (e.g. ["sh","check.sh"]) the <c>SupervisorDefinitionBuilder</c> bakes into the node's <c>acceptanceChecks</c>, enforced at the terminal stop (a non-zero exit fails the stop + withholds the reviewable head). Null / empty ⇒ omitted (byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }

    /// <summary>How an INDEPENDENT critic reviews each supervisor decision — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>decisionReviewMode</c>. <see cref="ReviewMode.None"/> (the default) ⇒ the builder omits the key (byte-identical). Inert on a non-supervisor projection.</summary>
    public ReviewMode DecisionReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The credentialed-model ROW the decision critic runs on — baked into the node's <c>reviewerModelId</c>. Null ⇒ omitted ⇒ the critic auto-picks the team brain. Only consulted when <see cref="DecisionReviewMode"/> is not None.</summary>
    public Guid? ReviewerModelId { get; init; }

    /// <summary>DC-2a: the operator's OWN pre-declared delivery preference — the <c>SupervisorDefinitionBuilder</c> bakes it into the node's <c>deliverySpec</c>, PER FIELD authoritative over the model's plan-time proposal (<c>SupervisorDeliveryClamp</c> enforces this at plan-persist time). Null ⇒ omitted (byte-identical). Inert on a non-supervisor projection.</summary>
    public DeliverySpec? DeliverySpec { get; init; }
}
