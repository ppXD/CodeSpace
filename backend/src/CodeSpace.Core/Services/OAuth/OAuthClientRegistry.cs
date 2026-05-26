using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

public sealed class OAuthClientRegistry : IOAuthClientRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<ProviderKind, IOAuthClient> _clients;

    public OAuthClientRegistry(IEnumerable<IOAuthClient> clients) { _clients = clients.ToDictionary(c => c.Kind); }

    public IOAuthClient Get(ProviderKind kind)
    {
        if (!_clients.TryGetValue(kind, out var client)) throw new NotSupportedException($"No OAuth client registered for provider {kind}");

        return client;
    }
}
