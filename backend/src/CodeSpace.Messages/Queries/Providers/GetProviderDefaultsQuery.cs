using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Providers;

public sealed record GetProviderDefaultsQuery : IQuery<ProviderDefaults>, IRequireAuthenticatedUser
{
    public required ProviderKind Provider { get; init; }
}
