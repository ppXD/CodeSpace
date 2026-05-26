using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Toggles the workflow's <c>enabled</c> flag. Disabling stops future triggers from firing but doesn't cancel in-flight runs.</summary>
public sealed record SetWorkflowEnabledCommand : ICommand<Unit>, IRequireTeamMembership
{
    /// <summary>Route-merged; non-required to keep body-only JSON binding from 400-failing. See <see cref="UpdateWorkflowCommand"/> note.</summary>
    public Guid WorkflowId { get; init; }
    public required bool Enabled { get; init; }
}
