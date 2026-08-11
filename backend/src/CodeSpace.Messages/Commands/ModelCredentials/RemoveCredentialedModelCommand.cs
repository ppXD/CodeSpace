using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Remove a model from a credential's maintained list. Both ids come from the route (Rule 17);
/// <see cref="ModelRowId"/> is the row's id, not the wire model id. Returns the removed row id.
/// </summary>
public sealed record RemoveCredentialedModelCommand : ICommand<Guid>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ModelsManage;

    public Guid ModelCredentialId { get; init; }
    public Guid ModelRowId { get; init; }
}
