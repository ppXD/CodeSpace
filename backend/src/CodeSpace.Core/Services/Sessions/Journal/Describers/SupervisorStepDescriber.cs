using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>Describes a supervisor decision event (the orchestration story beat) as a <c>decision</c> journal step, carrying the decision VERB (off the timeline kind) so the frontend renders a semantic pill under one "Supervisor" actor lane.</summary>
public sealed class SupervisorStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == SupervisorDecisionTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Decision) with { Verb = ReadVerb(e.Kind) };

    /// <summary>The decision verb off the timeline kind ("supervisor.spawn" → "spawn"). Null when the kind carries no verb suffix.</summary>
    private static string? ReadVerb(string kind)
    {
        var dot = kind.IndexOf('.');
        return dot >= 0 && dot < kind.Length - 1 ? kind[(dot + 1)..] : null;
    }
}
