using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Default <see cref="IRunActionCapabilityResolver"/>. The turn-level verbs are decided purely by
/// <see cref="WorkflowRunState"/> — the one source of truth the cancel CAS + the replay path already share — so the
/// room can never offer an action the engine would reject.
/// </summary>
public sealed class RunActionCapabilityResolver : IRunActionCapabilityResolver, IScopedDependency
{
    public IReadOnlyList<RoomAction> ResolveTurnActions(Guid runId, WorkflowRunStatus status, RoomPublishState? publish = null)
    {
        var terminal = WorkflowRunState.IsTerminal(status);
        var target = runId.ToString();

        // Continue resumes IN PLACE from where the run halted — the same-run-id revival ContinueRunAsync performs for a
        // stopped (Cancelled) or failed run. Offered ONLY there: Success has nothing to resume, an active run stops
        // first, and a Suspended run resumes via its wait / the stranded-continue control, not the turn footer.
        var continuable = status is WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure;

        var actions = new List<RoomAction>
        {
            new() { Kind = RoomActionKind.OpenTrace, Label = "View trace", Enabled = true, Target = target },

            new()
            {
                Kind = RoomActionKind.Continue,
                Label = "Continue",
                Enabled = continuable,
                DisabledReason = continuable ? null : "Only a stopped or failed turn can be resumed in place.",
                Target = target,
            },

            new()
            {
                Kind = RoomActionKind.RerunTurn,
                Label = "Rerun",
                Enabled = terminal,
                DisabledReason = terminal ? null : "This turn is still running — stop it first.",
                Target = target,
                Attempt = true,
            },

            new()
            {
                Kind = RoomActionKind.Stop,
                Label = "Stop",
                Enabled = !terminal,
                DisabledReason = terminal ? "This turn has already finished." : null,
                Target = target,
            },
        };

        // Omitted (not just disabled) when the caller didn't supply the signal — the light collapsed-card path skips
        // the extra ledger + manifest reads needed to compute it (PR-6). A focused turn always supplies it.
        if (publish is not null)
        {
            var hasPr = !string.IsNullOrEmpty(publish.OpenedPullRequestUrl);
            var canOpen = hasPr || publish.HasPublishedBranch;

            actions.Add(new RoomAction
            {
                Kind = RoomActionKind.OpenPullRequest,
                Label = hasPr ? "View PR" : "Open PR",
                Enabled = canOpen,
                DisabledReason = canOpen ? null : "This run has no published branch to open a pull request from.",
                Target = target,
                Url = publish.OpenedPullRequestUrl,
            });
        }

        return actions;
    }
}
