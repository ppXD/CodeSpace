using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="IPackHostAllowlist"/> — an https host allowlist seeded with <c>github.com</c> + <c>gitlab.com</c>
/// and extended by an operator env var (a self-hosted GitLab, an enterprise GitHub). Only an https URL whose host is
/// on the list is clonable; anything else (http, file://, ssh, a non-allowlisted/internal host) is refused — the
/// chosen egress posture that lets "paste a public github/gitlab URL" work while blocking SSRF / internal fetches.
/// </summary>
public sealed class PackHostAllowlist : IPackHostAllowlist, ISingletonDependency
{
    /// <summary>Operator-configurable EXTRA allowed hosts (comma-separated), ADDED to the github.com/gitlab.com defaults — e.g. a self-hosted GitLab. Pinned by a test (Rule 8): renaming it silently re-closes an operator's configured host.</summary>
    public const string AllowedHostsEnvVar = "CODESPACE_PACK_ALLOWED_HOSTS";

    private static readonly string[] DefaultHosts = { "github.com", "gitlab.com" };

    private readonly IReadOnlySet<string> _hosts;

    public PackHostAllowlist() : this(Environment.GetEnvironmentVariable(AllowedHostsEnvVar)) { }

    /// <summary>Test/Rule-8 seam — the raw env override is passed explicitly so the allowlist is pinned without mutating process env.</summary>
    internal PackHostAllowlist(string? rawAllowedHostsOverride) => _hosts = BuildHosts(rawAllowedHostsOverride);

    public bool IsAllowed(string url) => TryValidate(url, _hosts, out _);

    public void EnsureAllowed(string url)
    {
        if (!TryValidate(url, _hosts, out var reason)) throw new PackImportException(reason);
    }

    /// <summary>The defaults UNIONed with the operator override (blank/whitespace entries dropped); case-insensitive. Pure + internal so it's unit-pinned.</summary>
    internal static IReadOnlySet<string> BuildHosts(string? rawOverride)
    {
        var hosts = new HashSet<string>(DefaultHosts, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawOverride))
            foreach (var host in rawOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                hosts.Add(host);

        return hosts;
    }

    /// <summary>True when <paramref name="url"/> is a well-formed absolute https URL whose host is on <paramref name="hosts"/>; else false with an actionable <paramref name="reason"/>. Pure + internal so it's unit-pinned.</summary>
    internal static bool TryValidate(string url, IReadOnlySet<string> hosts, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = $"'{url}' is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"Only https pack sources are allowed (got scheme '{uri.Scheme}'). Paste an https git URL.";
            return false;
        }

        // Normalise a trailing-dot FQDN (github.com.) — a valid absolute DNS name git would clone — to the bare host
        // so it matches the allowlist; the trim only ever loosens toward a real host, never widens to a new one.
        if (!hosts.Contains(uri.Host.TrimEnd('.')))
        {
            reason = $"Host '{uri.Host}' is not in the pack-source allowlist [{string.Join(", ", hosts)}]. An operator can add it via the {AllowedHostsEnvVar} env var.";
            return false;
        }

        reason = "";
        return true;
    }
}
