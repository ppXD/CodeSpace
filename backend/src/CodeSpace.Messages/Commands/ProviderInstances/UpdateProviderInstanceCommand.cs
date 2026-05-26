using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.ProviderInstances;

/// <summary>
/// PATCH semantics: every field is optional. Null = "no change". Empty string on OAuth
/// secret is treated as null (no rotate) so an empty form field doesn't accidentally wipe
/// a stored secret. Provider kind cannot change post-creation — that's a delete + re-add.
/// </summary>
public sealed record UpdateProviderInstanceCommand : ICommand<Unit>, IRequireTeamMembership
{
    // Not marked `required` because the HTTP body never carries it — the controller reads
    // the id from the route segment and rewrites the command via `body with { ... }`. The
    // handler still receives a non-empty Guid for every legitimate call; tests should set
    // it explicitly when constructing the command in-process.
    public Guid ProviderInstanceId { get; init; }
    public string? DisplayName { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiUrl { get; init; }
    public string? WebUrl { get; init; }
    public string? OauthClientId { get; init; }

    /// <summary>Sending a non-empty value rotates the stored secret; null/empty keeps the existing one.</summary>
    public string? OauthClientSecret { get; init; }

    public string? OauthRedirectPath { get; init; }
    public IReadOnlyList<string>? OauthDefaultScopes { get; init; }
}
