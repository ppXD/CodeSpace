using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes a run-record event (the append-only lifecycle ledger) as a <c>lifecycle</c> journal step — or, when the
/// record is a MODEL CALL (an <c>interaction.*</c> record: the supervisor brain / a node LLM call), as a <c>model_call</c>
/// step, so the "deciding…" beats with their token cost read distinctly from run/node started/completed/failed.
/// </summary>
public sealed class LifecycleStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == RunRecordTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) =>
        JournalSteps.From(e, IsModelCall(e) ? JournalStepKinds.ModelCall : JournalStepKinds.Lifecycle);

    /// <summary>A model-call record — the run-record source stamps <c>Kind = RecordType</c> and surfaces exactly the two narrative interaction records (the <c>interaction.started</c> open bracket is Trace-only, never on the timeline), so matching those two canonical constants is precise.</summary>
    private static bool IsModelCall(RunTimelineEvent e) =>
        e.Kind == WorkflowRunRecordTypes.InteractionCompleted || e.Kind == WorkflowRunRecordTypes.InteractionFailed;
}
