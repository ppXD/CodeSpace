using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class PatPayload : CredentialPayload
{
    public override AuthType Type => AuthType.Pat;
    public required string Token { get; init; }
}
