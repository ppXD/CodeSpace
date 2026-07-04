using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes a flow.map dispatch event as a "dispatch" orchestration BEAT — so a NON-supervisor run's fan-out shows in
/// the ③ timeline (a "Dispatched N agents" beat with its agent cards) exactly like a supervisor spawn. Matches on the
/// map-dispatch source key alone, so it never races the run-record lifecycle describer. This is the generic-beat seam
/// paying off: the frontend renders any <c>Beat</c> step + its cards, so a map run lights up with no frontend change.
/// </summary>
public sealed class MapDispatchStepDescriber : IJournalStepDescriber
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == MapDispatchTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Decision) with { Beat = true, Verb = "dispatch" };
}
