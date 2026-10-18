using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an edit upgrades a git provider's address from http to https while tokens are connected through it, and the
/// new address does not answer HTTP from this server — a port that does not speak TLS, a certificate this server does not
/// trust, a host that is down. The upgrade is the one change of endpoint a connected token is allowed to follow, because it
/// sends the token nowhere new; but an address that cannot be reached breaks every connected token's calls, and putting it
/// back is itself a change of endpoint, refused while the tokens are connected. So the address is checked before it is kept,
/// and the stored one stays as it was.
///
/// <para>Names scheme, host and port alone: the address is whatever the owner typed and may carry userinfo or a query with
/// a credential in it, which this message, its details and the logs never repeat.</para>
/// </summary>
public sealed class ProviderEndpointUnreachableException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.ProviderEndpointUnreachable;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["provider"] = ProviderKind.ToString(), ["origin"] = Origin };

    public ProviderKind ProviderKind { get; }

    /// <summary>Scheme, host and port (when not the scheme's own) of the address that did not answer.</summary>
    public string Origin { get; }

    public ProviderEndpointUnreachableException(ProviderKind providerKind, Uri address, string reason) : base(BuildMessage(providerKind, address, reason))
    {
        ProviderKind = providerKind;
        Origin = ProviderBaseUrlRedirectsException.OriginOf(address);
    }

    private static string BuildMessage(ProviderKind provider, Uri address, string reason)
    {
        var origin = ProviderBaseUrlRedirectsException.OriginOf(address);

        return $"{origin} did not answer from this server ({reason}), so the {provider} provider's address was not changed to it: tokens are connected through this provider, and an address that cannot be reached would break every one of them with no way back short of disconnecting them. Check that {origin} serves HTTPS with a certificate this server trusts, then save again.";
    }
}
