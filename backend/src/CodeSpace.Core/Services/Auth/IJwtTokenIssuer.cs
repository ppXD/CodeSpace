using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Auth;

public interface IJwtTokenIssuer
{
    IssuedToken Issue(User user);
}

public sealed record IssuedToken
{
    public required string Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
