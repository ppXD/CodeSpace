using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches every supervisor decision step with the supervisor's AUTHORED rationale — the "why · Evidence: …" line the
/// model wrote for that plan / spawn / retry / merge / stop. Reads the run's decision tape and, for each row that carries
/// a rationale, attaches it keyed by the decision's timeline event id (<see cref="SupervisorDecisionTimelineMap.EventId"/>),
/// so the walk matches it to the same step the supervisor describer produced. The rationale lives at the payload root for
/// EVERY verb (<see cref="SupervisorOutcome.ReadRationale"/>), so this one source surfaces the chain-of-thought uniformly
/// across the whole trajectory — not just retries. A run with no supervisor tape contributes nothing (empty map).
/// </summary>
public sealed class RationaleFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public RationaleFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape)
        {
            var rationale = FormatRationale(SupervisorOutcome.ReadRationale(decision.PayloadJson));

            if (rationale is not null)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Rationale = rationale };
        }

        return facts;
    }

    /// <summary>The decision's structured rationale as one readable line — the reason, then the evidence it acted on. Null when the model authored neither. (The journal owns its copy; mirrors the room's soon-to-be-deleted formatter.)</summary>
    internal static string? FormatRationale((string? Why, string? Evidence) rationale)
    {
        var reason = string.IsNullOrWhiteSpace(rationale.Why) ? null : rationale.Why.Trim();
        var basis = string.IsNullOrWhiteSpace(rationale.Evidence) ? null : $"Evidence: {rationale.Evidence.Trim()}";

        return string.Join(" · ", new[] { reason, basis }.Where(part => part != null)) is { Length: > 0 } line ? line : null;
    }
}
