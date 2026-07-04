using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor PLAN beat with the model call that AUTHORED it (model · tokens · cost) — the same "via
/// &lt;model&gt;" attribution a flow.map planner beat shows, so a reader sees HOW the plan was authored right on the beat.
/// Reads the authoring usage the turn service folded into the plan decision's outcome
/// (<see cref="SupervisorOutcome.ReadModelUsage"/>) and keys it by the decision's timeline event id
/// (<see cref="SupervisorDecisionTimelineMap.EventId"/>). A pre-capture run (no folded usage) contributes nothing — the
/// beat stays bare, its model call still reachable as a model-call step in the fold. Cost rides the SHARED pricing (fail-open).
/// </summary>
public sealed class SupervisorPlanModelCallFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public SupervisorPlanModelCallFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape.Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan))
        {
            var usage = SupervisorOutcome.ReadModelUsage(decision.OutcomeJson);

            if (usage is not null)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { ModelCall = ToModelCall(usage) };
        }

        return facts;
    }

    /// <summary>Project the folded authoring usage to the shared <see cref="JournalModelCall"/> — the SAME row a model-call fold shows, minus a latency (the authoring span isn't captured on the decision). Cost via the shared pricing, fail-open null on an unpriced model.</summary>
    private static JournalModelCall ToModelCall(SupervisorModelUsage u)
    {
        var tokens = u.InputTokens is null && u.OutputTokens is null ? (int?)null : (u.InputTokens ?? 0) + (u.OutputTokens ?? 0);

        return new JournalModelCall
        {
            Purpose = "supervisor.plan",
            Model = u.Model,
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            Tokens = tokens,
            LatencyMs = null,
            CostUsd = tokens is null ? null : AgentCostPricing.CostUsd(u.Model, u.InputTokens ?? 0, u.OutputTokens ?? 0),
            Status = "completed",
        };
    }
}
