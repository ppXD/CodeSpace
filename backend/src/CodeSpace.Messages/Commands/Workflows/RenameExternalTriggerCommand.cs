using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Retires the URL configured by existing callers, so renaming must be explicit rather than a draft-save side effect.</summary>
public sealed record RenameExternalTriggerCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.WorkflowsWrite;
    public string Slug { get; init; } = string.Empty;
    public required string DesiredSlug { get; init; }
}
