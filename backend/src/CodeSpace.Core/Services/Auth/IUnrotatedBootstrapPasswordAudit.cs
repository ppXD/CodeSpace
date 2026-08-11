namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// Credential-hygiene audit: finds every user still carrying <c>password_must_change</c> and warns about each one.
/// The bootstrap admin seeded by migration 0006 trips this until an operator signs in and rotates — its credentials
/// are committed to source control, so anyone with read access can sign in until then.
/// </summary>
public interface IUnrotatedBootstrapPasswordAudit
{
    /// <summary>Warn once per unrotated user and return how many there were. Zero is the healthy state (and logs nothing).</summary>
    Task<int> WarnUnrotatedAsync(CancellationToken cancellationToken);
}
