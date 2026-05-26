using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.Core.Services.Credentials;

public interface ICredentialResolver
{
    CredentialPayload Resolve(Credential credential);
}
