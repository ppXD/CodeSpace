namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// Single entry point providers call to obtain a ResolvedAuth. Dispatches to the right
/// IProviderAuthStrategy by (Instance.Provider, Credential.AuthType). The provider class
/// never sees individual strategies — it stays auth-agnostic.
/// </summary>
public interface IProviderAuthResolver
{
    Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken);
}
