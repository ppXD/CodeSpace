using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>Launch a task, optionally consuming the matching server-owned preview decision. The reference is not consent or an execution grant.</summary>
public sealed record LaunchTaskCommand : TaskLaunchInput, ICommand<LaunchTaskResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.RunsLaunch;

    /// <summary>A preview reference for the same input, actor and team. Omitted keeps legacy routing; an invalid supplied reference fails closed.</summary>
    public Guid? RouteSnapshotId { get; init; }
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

    /// <summary>The <c>work_session.id</c> of the thread this launch opened — ALWAYS set; the run above is its first turn. A follow-up may continue the same session (a later slice).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The projection kind that built the run — the open string the registry resolved a builder by.</summary>
    public required string ProjectionKind { get; init; }

    /// <summary>The routing decision the run was projected from — carries the confirm-card escalation affordance for the UI.</summary>
    public required RoutePlan Route { get; init; }

    /// <summary>The resolved launch surface (an open string) the seed came from.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The external entity the task was launched from, when the seed carried one.</summary>
    public LinkedEntityRef? LinkedEntity { get; init; }
}
