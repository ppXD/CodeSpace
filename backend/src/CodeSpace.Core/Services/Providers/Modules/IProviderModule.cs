using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Modules;

/// <summary>
/// Pure descriptor — one file per provider that lists every class the provider contributes.
/// Replaces per-class IScopedDependency markers as the single source of truth for "what does
/// GitHub bring to the table". Adding a new provider is one new class, no edits elsewhere.
/// </summary>
public interface IProviderModule
{
    ProviderKind Kind { get; }

    /// <summary>Classes implementing one or more IProviderCapability sub-interfaces.</summary>
    IReadOnlyList<Type> Capabilities { get; }

    /// <summary>Classes implementing IProviderAuthStrategy. Each declares its own (Kind, AuthType) pair.</summary>
    IReadOnlyList<Type> AuthStrategies { get; }

    /// <summary>Classes implementing IProviderEventSubscription — one per (Kind, raw event name). Adding a new event = new class here, no other edits.</summary>
    IReadOnlyList<Type> EventSubscriptions { get; }

    /// <summary>Helper classes (signature verifier, event normalizer facade) consumed by capability classes via concrete-type constructor injection.</summary>
    IReadOnlyList<Type> AuxiliaryServices { get; }

    /// <summary>
    /// Scopes the OAuth init flow will request by default for this provider. Stored as the
    /// new ProviderInstance.OauthDefaultScopes; the frontend reads this list so we never
    /// hard-code scope names in two places. Empty means "ask provider for no specific scope"
    /// (legal for PKCE flows where the consent screen shows the app's pre-registered scope set).
    /// </summary>
    IReadOnlyList<string> DefaultOAuthScopes { get; }

    /// <summary>
    /// Per-capability scope contract: "to use TCapability against this provider, the granted
    /// scopes must satisfy ScopeRequirement". Keyed by capability interface (typeof
    /// IRepositoryCatalogCapability, etc.). A capability with no entry has no scope cost —
    /// any valid token works (e.g. ProbeCredential on GitHub needs nothing beyond the token).
    ///
    /// Used by IScopeChecker for pre-flight checks before bind and by the
    /// /api/credentials/{id}/capabilities endpoint that drives UI feature availability.
    /// </summary>
    IReadOnlyDictionary<Type, ScopeRequirement> CapabilityScopeRequirements { get; }
}
