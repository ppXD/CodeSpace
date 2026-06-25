using System.Text.Json;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The service-layer launch input (Rule 16 / 18.1, a pure data noun — no Mediation / ASP.NET types) the
/// <c>ITaskLaunchService</c> consumes. The handler maps the <c>LaunchTaskCommand</c> onto this, sourcing
/// <see cref="TeamId"/> from <c>ICurrentTeam</c> and <see cref="ActorUserId"/> from <c>ICurrentUser</c> — NEVER the
/// wire (tenancy fail-closed). The per-surface <c>ITaskLaunchSeedProvider</c> resolved by <see cref="SurfaceKind"/>
/// reads <see cref="SurfacePayload"/> (the folded <c>LaunchContext.Raw</c>) to derive the normalized seed; the core
/// never reads it.
/// </summary>
public sealed record TaskLaunchRequest
{
    /// <summary>The team (tenancy) the task runs under — sourced from <c>ICurrentTeam</c>, never the wire.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The user launching the task — sourced from <c>ICurrentUser</c>, never the wire.</summary>
    public required Guid ActorUserId { get; init; }

    /// <summary>The launch surface (an open <see cref="TaskLaunchSurfaceKinds"/> string) — the registry resolves a seed provider by it.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The operator's free-text task. Required by the chat surface; other surfaces may derive the goal from <see cref="SurfacePayload"/> instead.</summary>
    public string? TaskText { get; init; }

    /// <summary>When set, the launch CONTINUES this existing WorkSession (the run becomes its next top-level turn) rather than opening a new one. Validated TEAM-SCOPED + Open by the launch service. Null ⇒ open a new session.</summary>
    public Guid? ContinueSessionId { get; init; }

    /// <summary>The repository the task targets, when the operator named one. Validated TEAM-SCOPED by the launch service.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>ADDITIONAL repositories cloned alongside the primary <see cref="RepositoryId"/> for a coordinated multi-repo change. EVERY entry is validated TEAM-SCOPED (fail-closed). Null / empty ⇒ a single-repo run (byte-identical). Requires a primary <see cref="RepositoryId"/>.</summary>
    public IReadOnlyList<TaskRelatedRepository>? RelatedRepositories { get; init; }

    /// <summary>The base branch the work starts from, when named. Null → the repo's default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>An operator-chosen effort tier (open <see cref="Effort.TaskEffortModes"/> string). Null / <c>"auto"</c> ⇒ the router classifies.</summary>
    public string? RequestedEffort { get; init; }

    /// <summary>An operator-pinned recipe (open <see cref="Effort.TaskRecipeKinds"/> string). Null ⇒ the classifier's suggestion / the default recipe.</summary>
    public string? RequestedRecipe { get; init; }

    /// <summary>The autonomy tier the agent runs at (open tier-name string). Null / unrecognised → the safe <c>Standard</c> default.</summary>
    public string? Autonomy { get; init; }

    /// <summary>The operator's execution overrides (harness / model / persona / runner / credential). Defaults to all-absent.</summary>
    public TaskExecutionOverrides Overrides { get; init; } = new();

    /// <summary>The operator's safety-budget caps projected onto the router's <c>CapsOverride</c> seam (the numeric caps only — autonomy/approval merge tighten-only). Null ⇒ the effort preset's caps stand. The router TIGHTENS the preset with a set cap; a cost cap force-stops the run via the supervisor's bounds.</summary>
    public RouteCaps? CapsOverride { get; init; }

    /// <summary>The operator's allowed model pool (credentialed-model ROW ids) for the agents a Deep run dispatches — validated TEAM-SCOPED (fail-closed) + baked into the supervisor node's <c>allowedModelIds</c>. Null / empty ⇒ all the team's models (byte-identical). Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    /// <summary>The opaque per-surface payload (the folded <c>LaunchContext.Raw</c>) — ONLY the resolved seed provider reads it; the core never does. Defaults empty.</summary>
    public IReadOnlyDictionary<string, JsonElement> SurfacePayload { get; init; } = new Dictionary<string, JsonElement>();
}
