using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS publish half of the real executor (Rule 10 <c>.Publish.cs</c>, DC-2b): open a pull request
/// against the run's already-published branch(es) via the SAME shared <see cref="ISupervisorPullRequestOpener"/>
/// core the Room's Open-PR action uses, but driven from the LIVE turn context (<c>context.AgentProfile?.RepositoryId</c>
/// — the SAME pre-terminal source the stop-time acceptance grade's own target resolution uses) rather than a
/// terminal <c>WorkflowRun.OutputsJson</c> read. <see cref="SupervisorPublishPayload.TargetBranch"/>
/// (DC-2d) is the EFFECTIVE branch <see cref="SupervisorDeliveryGate"/> already resolved at substitution time —
/// read from THIS decision's own payload, never re-derived from <c>context.DeliverySpec</c> directly, so the
/// value the confirmation card showed a human and the value execution targets can never silently diverge; the
/// REJECTED stop's own summary (<see cref="SupervisorPublishPayload.StopSummary"/>, threaded the same way)
/// becomes the opened PR's title/body. Always SYNCHRONOUS — <see cref="SupervisorDeliveryGate"/> only ever
/// substitutes this decision for a <c>stop</c>, never proposes it independently.
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private async Task<SupervisorExecution> ExecutePublishAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var publish = Deserialize<SupervisorPublishPayload>(decision.PayloadJson) ?? new SupervisorPublishPayload();

        RoomPullRequestResult result;

        try
        {
            result = await _pullRequestOpener.OpenAsync(context.SupervisorRunId, context.TeamId, context.PriorDecisions, context.AgentProfile?.RepositoryId, publish.TargetBranch, publish.StopSummary, actorUserId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A live turn must NEVER crash on a publish failure — any unexpected throw (network, DB, a future
            // change to the opener's own contract) folds into a diagnosed Failed entry instead, so the NEXT
            // stop's gate reads it and parks with the reason rather than the turn loop dying mid-decision.
            _logger.LogWarning(ex, "Supervisor publish for run {RunId} threw unexpectedly — recording it as a diagnosed failure instead of crashing the turn", context.SupervisorRunId);

            result = new RoomPullRequestResult { PullRequests = new[] { new RoomPullRequestOpened { Alias = "", Disposition = RoomPullRequestDisposition.Failed, Error = ex.Message } } };
        }

        _logger.LogInformation("Supervisor publish for run {RunId} resolved {Count} target(s): {Dispositions}", context.SupervisorRunId, result.PullRequests.Count, string.Join(", ", result.PullRequests.Select(p => p.Disposition)));

        return SupervisorExecution.Synchronous(JsonSerializer.Serialize(result, AgentJson.Options));
    }
}
