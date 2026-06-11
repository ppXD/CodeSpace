using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Update a team model credential's display name / base URL, and OPTIONALLY rotate the key. The provider is
/// immutable (a different provider is a different credential). <see cref="ApiKey"/> follows write-only
/// semantics: null/blank = keep the existing key; a value = rotate to it. The service scopes the row to the
/// caller's team, so a foreign id is not updatable.
/// </summary>
public sealed record UpdateModelCredentialCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
}
