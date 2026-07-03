using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>Describes a supervisor decision event (the orchestration story beat) as a <c>decision</c> journal step.</summary>
public sealed class SupervisorStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == SupervisorDecisionTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Decision);
}
