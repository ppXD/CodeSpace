using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Enums;

namespace CodeSpace.UnitTests;

/// <summary>The unit-test composer stand-in: no recital, no receipts — every compose reads null, so a TurnService under test behaves byte-identically to pre-P5-6 (the recital is best-effort by contract). Enclosing-namespace visible to every unit-test file.</summary>
internal sealed class NullCompletionComposer : ICompletionAssessmentComposer
{
    public Task<ComposedAssessment?> ComposeAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult<ComposedAssessment?>(null);
    public Task<ComposedAssessment?> ComposeAsync(Guid workflowRunId, Guid teamId, WorkflowRunStatus assumeTerminalStatus, CancellationToken cancellationToken) => Task.FromResult<ComposedAssessment?>(null);
    public Task<ComposedAssessment?> ComposeIfStoppedNowAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult<ComposedAssessment?>(null);
}
