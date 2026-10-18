using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Asks a provider about a credential within the probe deadline (<see cref="CodeSpace.Messages.Dtos.Credentials.CredentialHealthDeadlines"/>).
/// A sibling of <see cref="ICredentialHealthService"/>, which keeps recorded health in step with it: whatever must ask about a
/// token BEFORE it is stored — the one a person pasted, the pair a sign-in was just issued — is bounded exactly as the sweep and
/// the add are, without having any use for the sweep.
/// </summary>
public interface IBoundedCredentialProbe
{
    /// <summary>
    /// What the provider says about <paramref name="credential"/> right now. A provider that does not answer in time is "no
    /// answer" (<see cref="CredentialProbeResult.IsValid"/> false, not rejected), however it ignores the caller's token. Nothing
    /// is read from or written to the database, and the credential may be a copy that exists nowhere. Its <c>Status</c> is set to
    /// Active on the instance given, so that a credential recorded as Error is asked as if it were not: pass one that is never saved.
    /// </summary>
    Task<CredentialProbeResult> ProbeAsync(ProviderInstance instance, Credential credential, CancellationToken cancellationToken);
}
