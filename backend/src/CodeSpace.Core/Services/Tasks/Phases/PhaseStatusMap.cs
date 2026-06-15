using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// Pure mappings from a substrate status to the UI render vocabulary <see cref="PhaseStatus"/>. The phase Kind
/// stays OPEN (never switched on), but the Status axis IS the one closed enum — so these two total functions are
/// the only place a NodeStatus / SupervisorDecisionStatus crosses into the render vocabulary. Unit-pinned. Any new
/// substrate enum value forces a compile-visible decision here.
/// </summary>
public static class PhaseStatusMap
{
    /// <summary>NodeStatus → PhaseStatus: Pending→Pending, Running→Active, Suspended→Waiting, Success→Succeeded, Failure→Failed, Skipped→Skipped.</summary>
    public static PhaseStatus FromNode(NodeStatus status) => status switch
    {
        NodeStatus.Pending => PhaseStatus.Pending,
        NodeStatus.Running => PhaseStatus.Active,
        NodeStatus.Suspended => PhaseStatus.Waiting,
        NodeStatus.Success => PhaseStatus.Succeeded,
        NodeStatus.Failure => PhaseStatus.Failed,
        NodeStatus.Skipped => PhaseStatus.Skipped,
        _ => PhaseStatus.Pending,
    };

    /// <summary>SupervisorDecisionStatus → PhaseStatus: Pending→Pending, AwaitingApproval→Waiting, Running→Active, Succeeded→Succeeded, Failed→Failed, Expired→Failed.</summary>
    public static PhaseStatus FromDecision(SupervisorDecisionStatus status) => status switch
    {
        SupervisorDecisionStatus.Pending => PhaseStatus.Pending,
        SupervisorDecisionStatus.AwaitingApproval => PhaseStatus.Waiting,
        SupervisorDecisionStatus.Running => PhaseStatus.Active,
        SupervisorDecisionStatus.Succeeded => PhaseStatus.Succeeded,
        SupervisorDecisionStatus.Failed => PhaseStatus.Failed,
        SupervisorDecisionStatus.Expired => PhaseStatus.Failed,
        _ => PhaseStatus.Pending,
    };
}
