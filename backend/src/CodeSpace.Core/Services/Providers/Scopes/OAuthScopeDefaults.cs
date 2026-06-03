namespace CodeSpace.Core.Services.Providers.Scopes;

/// <summary>
/// Derives the OAuth scope set a provider should REQUEST at consent time FROM its declared per-capability
/// scope requirements — so the request always covers every capability and can never DRIFT from the
/// requirement map (the single source of truth). Add a capability that needs a new scope and the OAuth
/// request grows to include it automatically; no second place to update.
///
/// For each capability we request its PREFERRED alternative — by convention <see cref="ScopeRequirement"/>
/// lists the broadest first (e.g. <c>repo</c> before <c>public_repo</c>, <c>api</c> before <c>read_api</c>),
/// so requesting the first alternative grants enough for every operation, not a read-only subset.
/// <paramref name="extra"/> adds scopes a provider needs that aren't a capability requirement (e.g. a
/// profile-read scope). Order is stable: capability scopes (in map order) then extras, de-duped case-insensitively.
/// </summary>
public static class OAuthScopeDefaults
{
    public static IReadOnlyList<string> Compute(IReadOnlyList<string> extra, IReadOnlyDictionary<Type, ScopeRequirement> capabilityScopeRequirements)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) && seen.Add(scope)) result.Add(scope);
        }

        foreach (var requirement in capabilityScopeRequirements.Values)
        {
            var preferred = requirement.Alternatives.Count > 0 ? requirement.Alternatives[0] : Array.Empty<string>();
            foreach (var scope in preferred) Add(scope);
        }

        foreach (var scope in extra) Add(scope);

        return result;
    }
}
