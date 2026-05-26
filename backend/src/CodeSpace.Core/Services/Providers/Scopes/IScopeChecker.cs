using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Scopes;

/// <summary>
/// Pure in-memory check: "given the scopes this credential was granted, can we use TCapability
/// against the provider?". Drives:
///   • Pre-flight assertion in RepositoryBindingService (fail fast before hitting the wire).
///   • The /api/credentials/{id}/capabilities endpoint that powers UI feature availability.
///
/// Does NOT call the provider — this is a static comparison against
/// <see cref="IProviderModule.CapabilityScopeRequirements"/>. Token validity is a separate
/// concern (ICredentialProbeCapability covers that). Both pre-flight steps run in sequence
/// when binding.
/// </summary>
public interface IScopeChecker
{
    /// <summary>Returns whether <paramref name="grantedScopes"/> satisfies the requirement.</summary>
    ScopeCheckOutcome Check(ProviderKind kind, Type capabilityType, IReadOnlyCollection<string>? grantedScopes);

    /// <summary>
    /// Convenience: throws <see cref="Messages.Exceptions.ProviderInsufficientScopeException"/>
    /// when the credential's scopes don't cover the capability. Use at the start of operations
    /// that need a specific capability — failure is structured + actionable.
    /// </summary>
    void EnsureCapability(Credential credential, ProviderKind kind, Type capabilityType);
}

public sealed record ScopeCheckOutcome
{
    public required bool IsSatisfied { get; init; }
    public IReadOnlyList<string> MissingScopes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GrantedScopes { get; init; } = Array.Empty<string>();
}
