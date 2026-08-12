using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Accounts;

/// <summary>Every account on the instance. Global-admin only — it spans teams by definition.</summary>
public sealed record ListAccountsQuery : IQuery<IReadOnlyList<AccountSummary>>, IRequireGlobalAdmin;
