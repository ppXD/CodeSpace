using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Create a team model credential. <see cref="ApiKey"/> is the plaintext secret — encrypted by the service
/// before persisting, never echoed back. Null/blank for a keyless provider (a local Ollama reached over
/// <see cref="BaseUrl"/>). Bound directly from the request body (Rule 17): the key is a single string, so no
/// structured-payload translation is needed.
/// </summary>
public sealed record AddModelCredentialCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
}
