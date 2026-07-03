using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>Describes an agent's own narrative event (a file edit, a test result, an error, its final summary) as an <c>agent</c> journal step.</summary>
public sealed class AgentEventStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == AgentEventTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Agent);
}
