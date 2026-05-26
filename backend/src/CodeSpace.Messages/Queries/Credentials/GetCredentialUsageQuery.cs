using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

/// <summary>
/// Pre-disconnect preview: how many repositories will lose their auth source if this
/// credential is revoked. Lets the UI surface impact in the confirm dialog so the user
/// understands what they're about to break — "Disconnect? 3 repositories will need a
/// new credential." Cheaper than running the actual revoke and reading the result.
/// </summary>
public sealed record GetCredentialUsageQuery : IQuery<CredentialUsage>, IRequireCredentialAccess
{
    public required Guid CredentialId { get; init; }
}

public sealed record CredentialUsage
{
    public required Guid CredentialId { get; init; }
    public required int ActiveRepositoryCount { get; init; }
}
