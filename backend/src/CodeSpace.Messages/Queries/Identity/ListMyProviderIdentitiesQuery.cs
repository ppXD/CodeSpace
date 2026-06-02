using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using MediatR;

namespace CodeSpace.Messages.Queries.Identity;

/// <summary>The caller's own live provider identities (Model B), newest first.</summary>
public sealed record ListMyProviderIdentitiesQuery : IRequest<IReadOnlyList<UserProviderIdentitySummary>>, IRequireTeamMembership;
