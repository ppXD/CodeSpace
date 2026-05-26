using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

public sealed record ListAccessibleRepositoriesQuery : IQuery<RemoteRepositoryPage>, IRequireCredentialAccess
{
    /// <summary>Default page size — matches the PR-list default and keeps the picker snappy even on accounts with thousands of repos.</summary>
    public const int DefaultPerPage = 30;

    /// <summary>Both providers cap at 100 server-side; the handler clamps anything higher.</summary>
    public const int MaxPerPage = 100;

    public required Guid CredentialId { get; init; }

    /// <summary>Server-side name filter. Null / whitespace = unfiltered browse.</summary>
    public string? Search { get; init; }

    /// <summary>1-based page index.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size — clamped to [1, MaxPerPage].</summary>
    public int PerPage { get; init; } = DefaultPerPage;
}
