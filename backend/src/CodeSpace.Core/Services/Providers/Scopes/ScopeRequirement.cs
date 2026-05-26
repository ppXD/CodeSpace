namespace CodeSpace.Core.Services.Providers.Scopes;

/// <summary>
/// A capability's scope requirement, expressed as <b>"any one of these alternatives"</b>.
/// Each alternative is itself a set of scopes that must ALL be granted together.
///
/// GitHub example for "list repos": <c>[[repo]] OR [[public_repo]]</c> — the user can grant
/// either the full <c>repo</c> scope or just <c>public_repo</c> (which limits visibility, but
/// is enough to enumerate public repositories).
///
/// GitLab example for "register webhook": <c>[[api]]</c> — there is no narrower alternative;
/// GitLab's <c>read_repository</c> and <c>read_api</c> both lack project-hooks write.
///
/// Semantics:
///   • <see cref="Alternatives"/> empty → always satisfied (no scope required).
///   • Any alternative whose scope set is a subset of <paramref name="grantedScopes"/> → satisfied.
///   • All comparisons are case-insensitive (providers vary on casing in the token response).
/// </summary>
public sealed record ScopeRequirement
{
    public static readonly ScopeRequirement None = new(Array.Empty<IReadOnlyList<string>>());

    public ScopeRequirement(IReadOnlyList<IReadOnlyList<string>> alternatives)
    {
        Alternatives = alternatives;
    }

    public IReadOnlyList<IReadOnlyList<string>> Alternatives { get; }

    /// <summary>Convenience — single alternative with one scope. <c>ScopeRequirement.Of("api")</c>.</summary>
    public static ScopeRequirement Of(string scope) => new(new IReadOnlyList<string>[] { new[] { scope } });

    /// <summary>Convenience — "any one of these single-scope alternatives". <c>ScopeRequirement.AnyOf("repo", "public_repo")</c>.</summary>
    public static ScopeRequirement AnyOf(params string[] scopes) => new(scopes.Select(s => (IReadOnlyList<string>)new[] { s }).ToList());

    /// <summary>True when <paramref name="grantedScopes"/> satisfies at least one alternative.</summary>
    public bool IsSatisfied(IReadOnlyCollection<string>? grantedScopes)
    {
        if (Alternatives.Count == 0) return true;

        var granted = NormalizeGranted(grantedScopes);

        return Alternatives.Any(alt => alt.All(s => granted.Contains(s.ToLowerInvariant())));
    }

    /// <summary>
    /// The first alternative that's missing scopes — used to build "you need: X, Y" error
    /// messages. Returns null when already satisfied. Picks the shortest alternative so the
    /// suggested fix is the least intrusive one.
    /// </summary>
    public IReadOnlyList<string>? MissingScopesForBestAlternative(IReadOnlyCollection<string>? grantedScopes)
    {
        if (Alternatives.Count == 0) return null;

        var granted = NormalizeGranted(grantedScopes);

        var bestAlternative = Alternatives
            .Select(alt => new { Alt = alt, Missing = alt.Where(s => !granted.Contains(s.ToLowerInvariant())).ToList() })
            .OrderBy(x => x.Missing.Count)
            .ThenBy(x => x.Alt.Count)
            .First();

        return bestAlternative.Missing.Count == 0 ? null : bestAlternative.Missing;
    }

    private static HashSet<string> NormalizeGranted(IReadOnlyCollection<string>? grantedScopes)
    {
        return grantedScopes == null ? new HashSet<string>() : new HashSet<string>(grantedScopes.Select(s => s.ToLowerInvariant()));
    }
}
