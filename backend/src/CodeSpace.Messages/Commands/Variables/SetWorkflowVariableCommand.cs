using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Messages.Commands.Variables;

/// <summary>
/// Upsert a workflow-scoped variable. Tenant guard: the service rejects with
/// <see cref="KeyNotFoundException"/> if the workflow doesn't belong to the caller's
/// current team. <see cref="WorkflowId"/> comes from the URL — controller does
/// <c>command with { WorkflowId = routeId }</c> before dispatch.
/// </summary>
public sealed record SetWorkflowVariableCommand : IRequest<Unit>, IRequireTeamMembership
{
    public Guid WorkflowId { get; init; }
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
    public string? Description { get; init; }
}
