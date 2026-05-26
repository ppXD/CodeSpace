using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

public sealed record TestRepositoryBindingCommand : ICommand<CredentialProbeResult>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}
