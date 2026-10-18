using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

/// <summary>
/// Pre-disconnect preview: what a Disconnect of this credential, asked by the caller right now, would do to the
/// repositories bound through it. Lets the UI surface impact in the confirm dialog so the user understands what they're
/// about to break — "Disconnect? 3 repositories will need a new credential" — or what will carry on without a break — "3
/// repositories will move to your other connection". Cheaper than running the actual revoke and reading the result.
/// </summary>
public sealed record GetCredentialUsageQuery : IQuery<CredentialUsage>, IRequireCredentialAccess
{
    public required Guid CredentialId { get; init; }
}

public sealed record CredentialUsage
{
    public required Guid CredentialId { get; init; }

    /// <summary>Working repositories the Disconnect would turn to "needs a new credential". Zero when they would follow another connection instead — see <see cref="WillMoveRepositoryCount"/>.</summary>
    public required int ActiveRepositoryCount { get; init; }

    /// <summary>Repositories the Disconnect would move, in the same unit of work, to the person's one other live connection (<see cref="WillMoveToDisplayName"/>) rather than break. Zero when nothing would move.</summary>
    public int WillMoveRepositoryCount { get; init; }

    /// <summary>The display name of the connection they would move to; null when nothing would move.</summary>
    public string? WillMoveToDisplayName { get; init; }
}
