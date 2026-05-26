using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class OAuthPayload : CredentialPayload
{
    public override AuthType Type => AuthType.OAuth;
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
