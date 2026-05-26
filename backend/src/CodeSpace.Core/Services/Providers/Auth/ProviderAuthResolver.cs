using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Auth;

public sealed class ProviderAuthResolver : IProviderAuthResolver, IScopedDependency
{
    private readonly IReadOnlyDictionary<(ProviderKind Kind, AuthType AuthType), IProviderAuthStrategy> _strategies;

    public ProviderAuthResolver(IEnumerable<IProviderAuthStrategy> strategies) { _strategies = strategies.ToDictionary(s => (s.Kind, s.AuthType)); }

    public async Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var key = (context.Instance.Provider, context.Credential.AuthType);

        if (!_strategies.TryGetValue(key, out var strategy)) throw new NotSupportedException($"No auth strategy registered for ({key.Provider}, {key.AuthType})");

        return await strategy.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
