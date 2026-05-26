using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.ProviderInstances;

/// <summary>
/// Pre-delete preview for a provider instance: how many repos are bound and how many
/// credentials would be cascade-revoked. UI uses this to put concrete numbers in the
/// "Remove provider?" confirm so the operator isn't surprised by a hidden cascade.
/// </summary>
public sealed record GetProviderInstanceUsageQuery : IQuery<ProviderInstanceUsage>, IRequireTeamMembership
{
    public required Guid ProviderInstanceId { get; init; }
}

public sealed record ProviderInstanceUsage
{
    public required Guid ProviderInstanceId { get; init; }
    public required int ActiveRepositoryCount { get; init; }
    public required int ActiveCredentialCount { get; init; }
}
