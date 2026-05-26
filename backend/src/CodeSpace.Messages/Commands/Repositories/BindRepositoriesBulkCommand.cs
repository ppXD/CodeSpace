using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

// All-or-nothing semantics: wrapped by TransactionalBehavior, so any failure rolls back ALL binds.
// For partial-success use case, call BindRepositoryCommand individually instead.
public sealed record BindRepositoriesBulkCommand : ICommand<BulkBindResult>, IRequireTeamMembership
{
    public required Guid ProviderInstanceId { get; init; }
    public required Guid CredentialId { get; init; }
    public required IReadOnlyList<string> ProjectIdentifiers { get; init; }
}
