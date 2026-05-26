using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

public abstract class CredentialPayload
{
    public abstract AuthType Type { get; }
}
