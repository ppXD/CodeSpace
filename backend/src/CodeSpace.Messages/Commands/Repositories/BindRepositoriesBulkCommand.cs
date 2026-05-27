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

    /// <summary>
    /// Phase 3.0 — the CodeSpace Project (NOT the provider-side "project" concept, which is
    /// <see cref="ProjectIdentifiers"/>) the bulk-bound repositories should be attached to.
    /// Null → the team's lazily-created Default project. Same id applies to every repo in
    /// the batch (the AddRepoModal picker shows one project for the whole bulk action).
    /// </summary>
    public Guid? ProjectId { get; init; }
}
