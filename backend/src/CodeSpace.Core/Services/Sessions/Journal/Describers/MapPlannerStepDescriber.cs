using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes a flow.map planner event as a "map_plan" orchestration BEAT — so a NON-supervisor run's plan shows in the ③
/// timeline (a "Planned N subtasks" beat with its plan card) exactly like a supervisor PLAN decision, just read-only (a
/// workflow planner's plan is never up for confirmation). The verb is DISTINCT from the supervisor's "plan" on purpose:
/// the frontend renders the same PLAN pill for both, but only "plan" is supervisor-distinctive for the actor lane — so a
/// pure map run reads "Workflow", not "Supervisor" (the exact parallel to map "dispatch" vs supervisor "spawn"). Matches
/// on the map-plan source key alone, so it never races the run-record lifecycle describer. The generic-beat seam paying
/// off: the frontend renders any <c>Beat</c> step + its plan, so a map run's plan lights up with no frontend change.
/// </summary>
public sealed class MapPlannerStepDescriber : IJournalStepDescriber
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == MapPlannerTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Decision) with { Beat = true, Verb = "map_plan" };
}
