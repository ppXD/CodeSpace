using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// The shared agent roll-up — a phase's <see cref="PhaseMetrics"/> derived from its agent refs by their GROUND-TRUTH
/// <see cref="AgentRunStatus"/>. ONE source of truth for EVERY phase source that fans out to agents (the structural
/// agent.code node + the supervisor decisions), so the succeeded / failed counts can never drift between sources —
/// the drift that made a finished agent node read "0/1" instead of "1/1" because the node source only filled
/// <see cref="PhaseMetrics.AgentCount"/>. (A flow.map node keeps its own engine count/failed roll-up, not this.)
/// </summary>
public static class PhaseAgentMetrics
{
    public static PhaseMetrics From(IReadOnlyList<PhaseAgentRef> agents) => new()
    {
        AgentCount = agents.Count,
        SucceededCount = agents.Count(a => a.Status == nameof(AgentRunStatus.Succeeded)),
        FailedCount = agents.Count(a => a.Status is nameof(AgentRunStatus.Failed) or nameof(AgentRunStatus.Cancelled) or nameof(AgentRunStatus.TimedOut)),
    };
}
