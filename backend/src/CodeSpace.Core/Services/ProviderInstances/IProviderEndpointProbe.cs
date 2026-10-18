using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.ProviderInstances;

/// <summary>
/// Asks a git provider's address, once, before it is stored. Whether it redirects to another origin — so the redirect is
/// named when the owner types it rather than as a 401 on every call afterwards — is best effort by contract: a host that is
/// down, slow, unresolvable or answers anything but a redirect leaves the URL as the owner wrote it. Whether it answers at
/// all is asked only of an http to https upgrade that connected tokens will follow, and there silence is the verdict.
/// </summary>
public interface IProviderEndpointProbe
{
    /// <summary>
    /// Returns when <paramref name="url"/> (the address provider calls go to) answers no redirect to another origin — or
    /// could not be asked, which is not a verdict on it. Throws <see cref="ProviderBaseUrlRedirectsException"/> naming the
    /// URL it pointed at when it does redirect there. Never sends a credential.
    /// </summary>
    Task EnsureNoRedirectToAnotherOriginAsync(ProviderKind provider, string url, CancellationToken cancellationToken);

    /// <summary>
    /// Returns when <paramref name="url"/> answers HTTP at all — any status, a redirect included. Throws
    /// <see cref="ProviderEndpointUnreachableException"/> when it does not within the probe's deadline (a TLS handshake that
    /// cannot complete, a host that is down or silent). Asked only of an http to https upgrade that connected tokens will
    /// follow, where the one thing worse than a refused edit is an address nothing answers at that cannot be edited back;
    /// a URL that is not an absolute http(s) one has nothing to answer and returns. Never sends a credential.
    /// </summary>
    Task EnsureAnswersAsync(ProviderKind provider, string url, CancellationToken cancellationToken);
}
