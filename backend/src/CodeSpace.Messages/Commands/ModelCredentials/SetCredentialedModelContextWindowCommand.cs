using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>Set or clear the total context capacity for one credentialed model row.</summary>
public sealed record SetCredentialedModelContextWindowCommand : ICommand<Guid>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ModelsManage;
    public Guid ModelCredentialId { get; init; }
    public Guid ModelRowId { get; init; }
    public int? ContextWindowTokens { get; init; }
}
