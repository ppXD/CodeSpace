namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Composes the deny-by-default egress allowlist HOST NAMES for an <c>AgentEgressPolicy.Allowlist</c> run (B3.3b): the
/// run's model-API host (the credential <c>BaseUrl</c>, or the provider's default endpoint when none is set) + each
/// repository's git host + the operator-configured extra hosts. Pure (URL → host); the runner later resolves these
/// names to IPs via <see cref="EgressHostResolver"/> at netns setup. De-duped + lower-cased; an unparseable URL is
/// skipped. The result is what makes a restricted run still able to reach its model and push its branch while every
/// other destination is dropped.
/// </summary>
public static class EgressAllowlistBuilder
{
    /// <summary>
    /// The default API host per provider tag, used when a credential has no custom <c>BaseUrl</c> (the provider's
    /// own endpoint). Keyed by <c>ResolvedModelCredential.Provider</c>. Pinned by a test (Rule 8) — a restricted run on
    /// the default endpoint can't reach its model if this host is wrong, so changing it is a visible decision. An
    /// unknown provider yields no auto model host (the operator must name it via extra hosts).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderDefaultHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Anthropic"] = "api.anthropic.com",
        ["OpenAI"] = "api.openai.com",
        ["OpenRouter"] = "openrouter.ai",
    };

    /// <summary>
    /// Build the allowlist host set: the model host (<paramref name="modelBaseUrl"/>'s host, else the provider default)
    /// + each <paramref name="repoCloneUrls"/> host + <paramref name="extraHosts"/>. De-duped (ordinal, lower-cased),
    /// blanks/unparseable dropped, order-preserving. May be EMPTY (no derivable host) — the caller must fail closed on
    /// that, never widen to full egress.
    /// </summary>
    public static IReadOnlyList<string> Build(string? modelBaseUrl, string? modelProvider, IEnumerable<string> repoCloneUrls, IReadOnlyList<string>? extraHosts)
    {
        var hosts = new List<string>();

        if (ModelHost(modelBaseUrl, modelProvider) is { } modelHost) hosts.Add(modelHost);

        foreach (var url in repoCloneUrls)
            if (HostOf(url) is { } gitHost) hosts.Add(gitHost);

        if (extraHosts is not null)
            foreach (var extra in extraHosts)
                if (Normalize(extra) is { } host) hosts.Add(host);

        return hosts.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The model-API host: the custom gateway <c>BaseUrl</c>'s host when set, else the provider's default endpoint host (null for an unknown provider with no custom base — the operator must name it via extra hosts).</summary>
    private static string? ModelHost(string? baseUrl, string? provider) =>
        HostOf(baseUrl) ?? (provider is { Length: > 0 } p && ProviderDefaultHosts.TryGetValue(p, out var host) ? host : null);

    /// <summary>Extract the lower-cased host from a URL: an http(s) URL via <see cref="Uri"/>, or an scp-style git remote (<c>git@host:path</c>). Null when blank or unparseable.</summary>
    internal static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } host)
            return host.ToLowerInvariant();

        // scp-style git remote: user@host:path/repo.git — no scheme, so Uri can't parse it. Take the host between @ and :.
        var at = trimmed.IndexOf('@');
        var colon = trimmed.IndexOf(':', at + 1);
        if (at >= 0 && colon > at + 1)
            return Normalize(trimmed[(at + 1)..colon]);

        return null;
    }

    /// <summary>Trim + lower-case a host name; null when blank.</summary>
    private static string? Normalize(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : host.Trim().ToLowerInvariant();
}
