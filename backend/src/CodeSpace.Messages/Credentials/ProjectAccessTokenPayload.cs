using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class ProjectAccessTokenPayload : CredentialPayload
{
    public override AuthType Type => AuthType.ProjectAccessToken;
    public required string Token { get; init; }
}
