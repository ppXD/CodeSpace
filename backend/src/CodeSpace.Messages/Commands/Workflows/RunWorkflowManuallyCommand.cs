using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Operator-initiated "Run" — same engine path as a triggered run, but the underlying
/// <c>workflow_run_request</c> carries <c>source_type="manual"</c> with the current user as
/// actor and an operator-supplied payload (defaults to empty object). Returns the new run
/// id so the UI can navigate to /runs/{id}.
/// </summary>
public sealed record RunWorkflowManuallyCommand : ICommand<Guid>, IRequireTeamMembership
{
    /// <summary>Route-merged; non-required to keep body-only JSON binding from 400-failing. See <see cref="UpdateWorkflowCommand"/> note.</summary>
    public Guid WorkflowId { get; init; }
    public JsonElement? Payload { get; init; }
}
