using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.UnitTests.Providers.Auth;

internal static class AuthTestContextFactory
{
    public static ProviderContext Build(ProviderKind kind, AuthType authType)
    {
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            Provider = kind,
            DisplayName = "test",
            BaseUrl = "https://test.local"
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = instance.TeamId,
            ProviderInstanceId = instance.Id,
            AuthType = authType,
            DisplayName = "test-cred",
            EncryptedPayload = string.Empty
        };

        return new ProviderContext(instance, credential);
    }
}
