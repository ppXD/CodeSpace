using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class GroupAccessTokenPayload : CredentialPayload
{
    public override AuthType Type => AuthType.GroupAccessToken;
    public required string Token { get; init; }
}
