using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Mark ONE model as a credential's default for an "auto" run (no pinned model). Both ids come from the route
/// (Rule 17); <see cref="ModelRowId"/> is the row's id. Setting one clears any other default on the same
/// credential. Returns the marked row id.
/// </summary>
public sealed record SetDefaultCredentialedModelCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid ModelCredentialId { get; init; }
    public Guid ModelRowId { get; init; }
}
