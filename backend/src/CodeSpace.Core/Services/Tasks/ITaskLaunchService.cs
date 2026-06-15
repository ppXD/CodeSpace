using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks;

/// <summary>
/// The L1 launch entry — ties seed → route → project → snapshot → run into one call (PR4). Given a
/// <see cref="TaskLaunchRequest"/> (team + actor already sourced from the current context, never the wire), it
/// resolves the per-surface seed provider, validates any repo TEAM-SCOPED (fail-closed), routes the effort,
/// builds the agent profile, projects + starts the snapshot run, and returns the
/// <see cref="LaunchTaskResult"/>. It ALWAYS runs (the <see cref="LaunchTaskResult.RunId"/> is always set); the
/// route's confirm-card escalation affordance rides along in <see cref="LaunchTaskResult.Route"/> for the UI —
/// PR4 does NOT block on confirm (the operator re-launches with an explicit effort to change).
/// </summary>
public interface ITaskLaunchService
{
    Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken);
}
