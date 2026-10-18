namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// An auth strategy whose token can be replaced after the provider refused it. A sibling of <see cref="IProviderAuthStrategy"/>
/// rather than a member of it: a static token (a personal, project or group access token) has nothing to replace it with, so
/// the question has no answer for most strategies, and the resolver asks only the ones that implement this.
/// </summary>
public interface IRecoverableProviderAuthStrategy : IProviderAuthStrategy
{
    /// <summary>The auth to present instead of <paramref name="rejectedToken"/>, which the provider answered 401; null when there is none to try.</summary>
    Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken);
}
