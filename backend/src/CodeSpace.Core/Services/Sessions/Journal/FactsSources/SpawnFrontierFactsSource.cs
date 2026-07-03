using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each spawn decision with the plan's BLOCKED FRONTIER as of that wave — the planned subtasks a DAG wasn't ready
/// to run yet (each with the unmet dependency it waits on), the "3 of 5 ready · #4 #5 deferred" the mock shows alongside a
/// wave. It replays the REAL <see cref="SupervisorDependencyGate.Frontier"/> over the decision tape AS OF each spawn (a
/// minimal context of the prior TERMINAL decisions — the gate reads only their kind + payload + outcome, which the
/// persisted rows carry), so it can never drift from the gate the server enforces.
///
/// <para>This is the FRONTIER (plan − satisfied − ready), NOT the per-spawn <c>Partition</c> ("this spawn requested X,
/// the server clamped to the ready subset"): the pre-clamp requested set is rewritten away at spawn time (NarrowSpawnPayload)
/// so it isn't recoverable at read-time, and the wave view wants the plan's not-yet-ready set regardless of what this
/// spawn asked for. Empty for a flat plan (no DAG → nothing is ever blocked) — the common case, so most spawns stay bare.</para>
/// </summary>
public sealed class SpawnFrontierFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public SpawnFrontierFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var spawn in tape.Where(d => d.DecisionKind == SupervisorDecisionKinds.Spawn))
        {
            var deferred = DeferredFor(tape, spawn);

            if (deferred.Count > 0)
                facts[SupervisorDecisionTimelineMap.EventId(spawn)] = new JournalStepFacts { Deferred = deferred };
        }

        return facts;
    }

    /// <summary>The blocked frontier the gate reports as of this spawn — the planned, not-yet-accepted subtasks whose dependencies weren't met, so the DAG wasn't ready to run them at this wave (independent of what this spawn requested — that's the whole-plan frontier). Only the prior TERMINAL decisions count (an in-flight one isn't a settled fact), mirroring how the live turn rehydrates its context.</summary>
    private static IReadOnlyList<JournalDeferredSubtask> DeferredFor(IReadOnlyList<SupervisorDecisionRecord> tape, SupervisorDecisionRecord spawn)
    {
        var prior = tape
            .Where(r => r.Sequence < spawn.Sequence && SupervisorDecisionStateMachine.IsTerminal(r.Status))
            .Select(ToPriorDecision)
            .ToList();

        var (_, blocked) = SupervisorDependencyGate.Frontier(new SupervisorTurnContext { PriorDecisions = prior });

        return blocked.Select(b => new JournalDeferredSubtask { SubtaskId = b.Id, WaitingOn = b.WaitingOn }).ToList();
    }

    /// <summary>The minimal tape-row → prior-decision projection the gate needs (kind + payload + outcome). Mirrors <c>SupervisorTurnService.ToPriorDecision</c> — the gate reads no other field.</summary>
    private static SupervisorPriorDecision ToPriorDecision(SupervisorDecisionRecord row) => new()
    {
        Id = row.Id,
        Sequence = row.Sequence,
        DecisionKind = row.DecisionKind,
        Status = row.Status,
        PayloadJson = row.PayloadJson,
        OutcomeJson = row.OutcomeJson,
        Error = row.Error,
    };
}
