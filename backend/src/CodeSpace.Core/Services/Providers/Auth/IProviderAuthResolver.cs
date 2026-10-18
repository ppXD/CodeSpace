namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// Single entry point providers call to obtain a ResolvedAuth. Dispatches to the right
/// IProviderAuthStrategy by (Instance.Provider, Credential.AuthType). The provider class
/// never sees individual strategies — it stays auth-agnostic.
/// </summary>
public interface IProviderAuthResolver
{
    Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The caller presented <paramref name="rejectedToken"/>, which <see cref="ResolveAsync"/> handed out, and the provider
    /// answered 401. Returns the auth to retry with once — another caller's newer token adopted from the stored credential, or
    /// a rotation this one made — or null when the rejection stands: a static token, a credential that has nothing to rotate
    /// with, or one rotated moments ago. A resolver with no way to recover answers null, which is every caller's cue to surface
    /// the 401 as it always did.
    /// </summary>
    Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken) => Task.FromResult<ResolvedAuth?>(null);
}
