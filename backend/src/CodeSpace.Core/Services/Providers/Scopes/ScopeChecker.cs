using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Providers.Scopes;

public sealed class ScopeChecker : IScopeChecker, ISingletonDependency
{
    private readonly IProviderModuleCatalog _modules;

    public ScopeChecker(IProviderModuleCatalog modules) { _modules = modules; }

    public ScopeCheckOutcome Check(ProviderKind kind, Type capabilityType, IReadOnlyCollection<string>? grantedScopes)
    {
        var requirement = ResolveRequirement(kind, capabilityType);

        var granted = grantedScopes ?? Array.Empty<string>();

        if (requirement.IsSatisfied(granted))
            return new ScopeCheckOutcome { IsSatisfied = true, GrantedScopes = granted.ToList() };

        var missing = requirement.MissingScopesForBestAlternative(granted) ?? Array.Empty<string>();
        return new ScopeCheckOutcome { IsSatisfied = false, MissingScopes = missing, GrantedScopes = granted.ToList() };
    }

    public void EnsureCapability(Credential credential, ProviderKind kind, Type capabilityType)
    {
        // Non-OAuth credentials (PAT, project access token, etc.) have no scope metadata on
        // this side — the user-side token decides what's allowed. We can't pre-check those;
        // any 403 from the wire flows through the per-provider error mapper instead.
        if (credential.AuthType != AuthType.OAuth) return;

        var outcome = Check(kind, capabilityType, credential.Scopes);
        if (outcome.IsSatisfied) return;

        throw new ProviderInsufficientScopeException(kind, capabilityType.Name, outcome.MissingScopes, outcome.GrantedScopes);
    }

    private ScopeRequirement ResolveRequirement(ProviderKind kind, Type capabilityType)
    {
        var module = _modules.Get(kind);

        // Unknown provider = unknown rules. Treat as "no requirement" so callers don't
        // accidentally over-restrict when a future provider's module hasn't shipped yet.
        if (module == null) return ScopeRequirement.None;

        return module.CapabilityScopeRequirements.TryGetValue(capabilityType, out var req) ? req : ScopeRequirement.None;
    }
}
