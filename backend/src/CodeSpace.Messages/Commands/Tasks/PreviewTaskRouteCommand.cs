using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>
/// PREVIEW the route a launch would take, WITHOUT launching it — the read-only twin of
/// <see cref="LaunchTaskCommand"/>. It runs the same seed provider + the same effort router over the same
/// request mapping and returns the resulting <c>RoutePlan</c>; no session is opened, no run is staged, nothing
/// is persisted. It exists because the router already builds a confirm card for a low-confidence or
/// risky-side-effect AUTO route and, before this, nothing could show it before the run had already started.
///
/// <para>Only the fields the ROUTE depends on are carried: the goal + repo + base branch the seed is built from,
/// and the operator's effort / recipe / caps overrides. Execution overrides (harness, model, persona, autonomy,
/// review modes, quality tier) never reach the router, so they are deliberately absent — a preview command that
/// mirrored the whole launch body would imply this predicts more than it does.</para>
///
/// <para>Tenancy: <see cref="IRequireTeamPermission"/>; the team comes from <c>ICurrentTeam</c>, never this body.
/// Every repository named is validated TEAM-SCOPED by the SAME guard the launch path uses — a foreign repo is an
/// indistinguishable not-found, never a cross-team read.</para>
/// </summary>
public sealed record PreviewTaskRouteCommand : ICommand<TaskRoutePreviewResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.RunsLaunch;

    /// <summary>The operator's free-text task — the goal the seed provider normalizes and the classifier reads.</summary>
    public required string TaskText { get; init; }

    /// <summary>The launch surface (an open <see cref="TaskLaunchSurfaceKinds"/> string) whose seed provider normalizes the request. Defaults to <c>chat</c>, exactly like the launch command's default.</summary>
    public string SurfaceKind { get; init; } = TaskLaunchSurfaceKinds.Chat;

    /// <summary>The repository the task targets, when named. Validated TEAM-SCOPED; a foreign repo is a clear not-found.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>ADDITIONAL repositories the launch would clone alongside the primary. Every entry is validated TEAM-SCOPED, exactly like the launch path. Null / empty ⇒ single-repo.</summary>
    public IReadOnlyList<TaskRelatedRepository>? RelatedRepositories { get; init; }

    /// <summary>The base branch the work would start from, when named. Null → the repo's default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The operator's chosen effort tier (open string). Null / <c>"auto"</c> ⇒ the classifier runs and the preview may carry a confirm card; anything else is an explicit decision and never confirms.</summary>
    public string? Effort { get; init; }

    /// <summary>An operator-pinned recipe (open string). Null ⇒ the classifier's suggestion / the default recipe.</summary>
    public string? Recipe { get; init; }

    /// <summary>The deliverable SHAPE a prior preview classified for this same task, echoed back — the SAME field the launch carries, so a preview under an explicit tier predicts the shape the launch would actually run. Null / blank ⇒ nothing carried.</summary>
    public string? DeliverableShape { get; init; }

    /// <summary>The operator's safety-budget caps, merged onto the resolved preset's caps exactly as a launch would merge them — so the previewed bounds are the bounds the run would get.</summary>
    public TaskCapsOverride? Caps { get; init; }

    /// <summary>The autonomy ceiling the operator pinned; rides the same tighten-only <c>RouteCaps</c> seam a launch uses. Null / blank ⇒ the preset's ceiling.</summary>
    public string? AutonomyCeiling { get; init; }
}
