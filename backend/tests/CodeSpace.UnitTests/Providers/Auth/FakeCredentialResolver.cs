using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;

namespace CodeSpace.UnitTests.Providers.Auth;

/// <summary>
/// Hand-rolled ICredentialResolver substitute. The project does not use a mocking library —
/// see the existing Capabilities tests for the same convention.
/// </summary>
public sealed class FakeCredentialResolver : ICredentialResolver
{
    private readonly CredentialPayload _payload;

    public FakeCredentialResolver(CredentialPayload payload) { _payload = payload; }

    public CredentialPayload Resolve(Credential credential) => _payload;
}
