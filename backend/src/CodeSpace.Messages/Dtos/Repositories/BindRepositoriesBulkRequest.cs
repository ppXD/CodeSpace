namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// Service-layer DTO for <c>IRepositoryBindingService.BindManyAsync</c>. Mirrors the shape
/// of <see cref="BindRepositoryRequest"/> but with a list of identifiers — every identifier
/// gets bound against the same <c>(TeamId, ProviderInstanceId, CredentialId, ProjectId)</c>
/// triple (the AddRepoModal picker shows one provider + project per bulk action).
///
/// <para><b>Contract</b>: all-or-nothing. Failure of any single bind throws and the
/// <c>TransactionalBehavior</c> rolls back every prior bind in the same call. Callers
/// needing partial-success semantics should iterate <see cref="BindRepositoryRequest"/>
/// themselves and own their own rollback / error-aggregation policy.</para>
/// </summary>
public sealed record BindRepositoriesBulkRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public required Guid CredentialId { get; init; }
    public required IReadOnlyList<string> ProjectIdentifiers { get; init; }

    /// <summary>
    /// Phase 3.0 — the CodeSpace Project (NOT the provider-side "project" concept, which is
    /// <see cref="ProjectIdentifiers"/>) every repository in the batch should be attached to.
    /// Null → each bind falls through to the team's lazily-created Default project.
    /// </summary>
    public Guid? ProjectId { get; init; }
}
