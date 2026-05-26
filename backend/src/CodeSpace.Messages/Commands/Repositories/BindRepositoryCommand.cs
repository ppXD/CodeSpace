using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

public sealed record BindRepositoryCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required Guid ProviderInstanceId { get; init; }
    public required Guid CredentialId { get; init; }
    public required string ProjectIdentifier { get; init; }
}
