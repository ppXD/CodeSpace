using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>
/// The GENERIC L1 task-launch command — the single entry that ties seed → route → project → snapshot → run into
/// one call (PR4). Dispatched at <c>POST /api/tasks</c>; the thin handler folds it into a <c>TaskLaunchRequest</c>
/// and delegates to <c>ITaskLaunchService</c>, which ALWAYS produces a running snapshot run (no Workflow row, no
/// feature flag — the endpoint is live).
///
/// <para>Tenancy: <see cref="IRequireTeamMembership"/>; the team is resolved from <c>ICurrentTeam</c> and the actor
/// from <c>ICurrentUser</c> in the handler — NEVER this body (fail-closed). A <see cref="RepositoryId"/> is validated
/// TEAM-SCOPED by the service; a repo outside the team is a clear not-found, never a cross-team leak.</para>
///
/// <para><see cref="Effort"/> / <see cref="Autonomy"/> / <see cref="Harness"/> / <see cref="RunnerKind"/> /
/// <see cref="SurfaceKind"/> are OPEN STRINGS (no enum) — a new tier / surface is a new const, never a core-enum edit.
/// PR4 intentionally DEFERS the supervisor / cost inputs (ApprovalPolicy + MaxParallelism / MaxTotalSpawns /
/// MaxCostUsd): they ride the router's <c>CapsOverride</c> seam a later PR fills, which PR4 leaves unused.</para>
/// </summary>
public sealed record LaunchTaskCommand : ICommand<LaunchTaskResult>, IRequireTeamMembership
{
    /// <summary>The operator's free-text task. Required by the chat surface; another surface may derive the goal from <see cref="LaunchContext"/> instead.</summary>
    public required string TaskText { get; init; }

    /// <summary>The repository the task targets, when named. Validated TEAM-SCOPED by the service; a foreign repo is a clear not-found.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The base branch the work starts from, when named. Null → the repo's default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>An operator-chosen effort tier (open string). Null / <c>"auto"</c> ⇒ the router classifies + rides a confirm card back.</summary>
    public string? Effort { get; init; }

    /// <summary>The autonomy tier the agent runs at (open string). Null / unrecognised → the safe <c>Standard</c> default.</summary>
    public string? Autonomy { get; init; }

    /// <summary>The harness the agent runs on (open string). Null / blank → the projection's harness default.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id within the harness's catalog. Null / blank → the persona's model → the harness default.</summary>
    public string? Model { get; init; }

    /// <summary>The Agent persona (<c>AgentDefinition</c>) the agent embodies. Null → a pure-inline run.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>The sandbox runner the agent executes on (open string). Null → the executor's default.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>The <c>ModelCredential</c> reference the agent authenticates with. Null → the persona default → the team/operator fallback.</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>The launch surface (an open <see cref="TaskLaunchSurfaceKinds"/> string). Defaults to <c>chat</c> — the registry resolves a seed provider by it.</summary>
    public string SurfaceKind { get; init; } = TaskLaunchSurfaceKinds.Chat;

    /// <summary>The opaque per-surface payload. The core never reads it — only the resolved seed provider does (the handler folds its <c>Raw</c> into the request's surface payload). Null for a free-text chat launch.</summary>
    public LaunchContext? LaunchContext { get; init; }
}

/// <summary>
/// The launch outcome (Rule 18.1, a pure data noun) — the <see cref="RunId"/> the caller tracks the run by, the
/// <see cref="ProjectionKind"/> that built it, the full <see cref="Route"/> (so the UI can show the
/// <c>NeedsConfirmCard</c> / <c>Confirm</c> escalation affordance — PR4 does NOT block on confirm; the operator
/// re-POSTs with an explicit <c>Effort</c> to change), the resolved <see cref="SurfaceKind"/>, and the linked entity
/// the seed carried. No <c>LaunchEnabled</c> / <c>WorkflowId</c> — there is no flag and no Workflow row.
/// </summary>
public sealed record LaunchTaskResult
{
    /// <summary>The <c>workflow_run.id</c> of the started snapshot run — ALWAYS set (the launch always runs).</summary>
    public required Guid RunId { get; init; }

    /// <summary>The projection kind that built the run — the open string the registry resolved a builder by.</summary>
    public required string ProjectionKind { get; init; }

    /// <summary>The routing decision the run was projected from — carries the confirm-card escalation affordance for the UI.</summary>
    public required RoutePlan Route { get; init; }

    /// <summary>The resolved launch surface (an open string) the seed came from.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The external entity the task was launched from, when the seed carried one.</summary>
    public LinkedEntityRef? LinkedEntity { get; init; }
}
