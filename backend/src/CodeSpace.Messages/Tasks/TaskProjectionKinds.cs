namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The OPEN-STRING projection kinds a <c>RoutePlan</c> may route at — the wire value a
/// <c>TaskBuildContext</c> carries on <c>Route.ProjectionKind</c> and the registry resolves a builder by.
/// Consts (NOT an enum, Rule 18.1) so a new projection strategy is a new const + a new builder folder, never
/// a core-enum edit. <see cref="SingleAgent"/> is the only kind with an implementation in this PR; the rest
/// are RESERVED names later PRs add builders for (plan-map-synthesize, the coordinated loop, the durable
/// supervisor) — they exist now only so the router/recipe layer can refer to them by a stable string.
/// </summary>
public static class TaskProjectionKinds
{
    /// <summary>One agent works the whole task in a single <c>agent.code</c> step — the only projection with a builder in this PR.</summary>
    public const string SingleAgent = "single-agent";

    /// <summary>RESERVED: a planner fans the task out over a <c>flow.map</c> then a synthesizer reduces. No builder yet.</summary>
    public const string PlanMapSynth = "plan-map-synth";

    /// <summary>Like <see cref="PlanMapSynth"/>, but the planner AUTHORS a per-subtask <c>mode</c> (research/code) the body maps to permissions — the model decides each agent's intent. Opt-in (no recipe serves a tier).</summary>
    public const string PlanMapDynamic = "plan-map-dynamic";

    /// <summary>RESERVED: a coordinated multi-round loop over a checkpoint coordinator. No builder yet.</summary>
    public const string CoordinatedLoop = "coordinated-loop";

    /// <summary>RESERVED: a continuous durable <c>agent.supervisor</c> lane. No builder yet.</summary>
    public const string Supervisor = "supervisor";
}
