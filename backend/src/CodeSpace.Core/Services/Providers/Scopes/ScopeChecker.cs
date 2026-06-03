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
        // Pre-check ANY credential whose scopes we KNOW. OAuth captures them at consent; PATs capture
        // them at link time too (GitLab personal_access_tokens/self, GitHub X-OAuth-Scopes). The auth
        // TYPE is irrelevant — the scope DATA is what decides — so a read-only PAT is caught here
        // rather than failing mid-run at the wire.
        //
        // null Scopes = UNKNOWN (a token type that can't expose them, or one linked before capture
        // existed): skip and let any wire 403 flow through the per-provider error mapper. This mirrors
        // CredentialService's capability-warning semantics exactly — the surfaced warning and this
        // runtime gate read identical scope data and treat null identically.
        if (credential.Scopes == null) return;

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
