using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>Describes a side-effecting tool-call event (what the agent did to the world) as a <c>tool</c> journal step.</summary>
public sealed class ToolStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == ToolCallTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Tool);
}
