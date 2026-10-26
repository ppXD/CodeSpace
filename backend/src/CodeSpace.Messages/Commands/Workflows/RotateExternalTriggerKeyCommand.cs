using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Immediately invalidates callers' existing credentials without a grace period; ordinary saves must never do this.</summary>
public sealed record RotateExternalTriggerKeyCommand : ICommand<string>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.WorkflowsWrite;
    public required Guid TriggerId { get; init; }
}
