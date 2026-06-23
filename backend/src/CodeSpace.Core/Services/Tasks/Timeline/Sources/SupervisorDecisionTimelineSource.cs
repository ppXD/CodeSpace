using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The SUPERVISOR-LEDGER timeline source — it reuses <see cref="ISupervisorDecisionLog.GetForRunAsync"/> (team-scoped,
/// ordered by Sequence — the SAME read <c>SupervisorPhaseSource</c> uses) and projects ONE timeline event per
/// supervisor decision (plan / spawn / retry / ask_human / merge / resolve / stop). A non-supervisor run has an empty
/// ledger and contributes nothing. READ-ONLY — a drop-in source the projector fans out automatically.
/// </summary>
public sealed class SupervisorDecisionTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly ISupervisorDecisionLog _ledger;

    public SupervisorDecisionTimelineSource(ISupervisorDecisionLog ledger) { _ledger = ledger; }

    public string SourceKey => SupervisorDecisionTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var decisions = await _ledger.GetForRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        return decisions.Select(SupervisorDecisionTimelineMap.ToEvent).ToList();
    }
}
