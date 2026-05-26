using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public sealed class GitHubAppPayload : CredentialPayload
{
    public override AuthType Type => AuthType.GitHubApp;
    public required long InstallationId { get; init; }
    public required long AppId { get; init; }
    public required string PrivateKeyPem { get; init; }
}
