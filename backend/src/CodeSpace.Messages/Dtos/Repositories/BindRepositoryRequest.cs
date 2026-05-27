namespace CodeSpace.Messages.Dtos.Repositories;

public sealed record BindRepositoryRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ProviderInstanceId { get; init; }
    public required Guid CredentialId { get; init; }
    public required string ProjectIdentifier { get; init; }

    /// <summary>
    /// Phase 3.0 — the CodeSpace Project the repository should be attached to (NOT the
    /// remote-provider concept, which is <see cref="ProjectIdentifier"/>). When null the
    /// service falls back to the team's "default" project (auto-created per team).
    /// </summary>
    public Guid? ProjectId { get; init; }
}
