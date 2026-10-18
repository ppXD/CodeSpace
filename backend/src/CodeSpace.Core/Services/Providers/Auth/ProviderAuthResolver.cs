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

        var auth = await strategy.ResolveAsync(context, cancellationToken).ConfigureAwait(false);

        // Every token handed out here is one the provider may echo back in an error — remember it beside the instance
        // so the resilience layer's SDK-error translation can strike it before a caller is shown the failure.
        PresentedProviderCredentials.Remember(context.Instance, auth.Token);

        return auth;
    }

    public async Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken)
    {
        var key = (context.Instance.Provider, context.Credential.AuthType);

        if (!_strategies.TryGetValue(key, out var strategy) || strategy is not IRecoverableProviderAuthStrategy recoverable) return null;

        var auth = await recoverable.InvalidateAsync(context, rejectedToken, cancellationToken).ConfigureAwait(false);

        if (auth is not null) PresentedProviderCredentials.Remember(context.Instance, auth.Token);

        return auth;
    }
}
