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
    public IReadOnlyList<RoomAction> ResolveTurnActions(Guid runId, WorkflowRunStatus status)
    {
        var terminal = WorkflowRunState.IsTerminal(status);
        var target = runId.ToString();

        return new[]
        {
            new RoomAction { Kind = RoomActionKind.OpenTrace, Label = "View trace", Enabled = true, Target = target },

            new RoomAction
            {
                Kind = RoomActionKind.RerunTurn,
                Label = "Rerun",
                Enabled = terminal,
                DisabledReason = terminal ? null : "This turn is still running — stop it first.",
                Target = target,
                Attempt = true,
            },

            new RoomAction
            {
                Kind = RoomActionKind.Stop,
                Label = "Stop",
                Enabled = !terminal,
                DisabledReason = terminal ? "This turn has already finished." : null,
                Target = target,
            },
        };
    }
}
