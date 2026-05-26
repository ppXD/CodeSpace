using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class SshKeyPayload : CredentialPayload
{
    public override AuthType Type => AuthType.SshKey;
    public required string PrivateKeyPem { get; init; }
    public string? Passphrase { get; init; }
}
