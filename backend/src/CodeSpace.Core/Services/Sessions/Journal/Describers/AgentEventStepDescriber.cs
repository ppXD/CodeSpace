using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes an agent's own narrative event as a journal step. A REASONING event (the agent's thinking block) becomes a
/// <c>thinking</c> step — the folded chain-of-thought beat — so it reads distinctly from the agent's <c>agent</c> narrative
/// events (a file edit, a test result, an error, its final summary). Sub-classifies on the emitted kind, mirroring how the
/// lifecycle describer splits <c>model_call</c> out of run-record events.
/// </summary>
public sealed class AgentEventStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == AgentEventTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) =>
        JournalSteps.From(e, e.Kind == AgentEventTimelineMap.ReasoningKind ? JournalStepKinds.Thinking : JournalStepKinds.Agent);
}
