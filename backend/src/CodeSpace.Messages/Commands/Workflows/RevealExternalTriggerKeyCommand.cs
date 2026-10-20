using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Exposes an anonymous-execution bearer credential, so membership alone must not grant Viewers access.</summary>
public sealed record RevealExternalTriggerKeyCommand : ICommand<string>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.WorkflowsWrite;
    public string Slug { get; init; } = string.Empty;
}
