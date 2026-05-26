using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

public interface ICredentialPayloadSerializer
{
    string Serialize(CredentialPayload payload);
    CredentialPayload Deserialize(AuthType type, string json);
    TPayload Deserialize<TPayload>(AuthType type, string json) where TPayload : CredentialPayload;
}
