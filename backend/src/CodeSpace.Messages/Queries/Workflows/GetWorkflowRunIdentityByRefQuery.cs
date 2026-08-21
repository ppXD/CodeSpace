using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Resolve a team-owned Workflow Run's bounded canonical identity by run number or legacy GUID.</summary>
public sealed record GetWorkflowRunIdentityByRefQuery : IQuery<WorkflowRunIdentity?>, IRequireTeamMembership
{
    public required string IdOrNumber { get; init; }
}
