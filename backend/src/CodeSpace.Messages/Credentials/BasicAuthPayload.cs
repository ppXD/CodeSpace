using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class BasicAuthPayload : CredentialPayload
{
    public override AuthType Type => AuthType.BasicAuth;
    public required string Username { get; init; }
    public required string Password { get; init; }
}
