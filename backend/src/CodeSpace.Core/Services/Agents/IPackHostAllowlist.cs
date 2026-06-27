namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The egress guard for URL pack import: which hosts the worker may <c>git clone</c> a pasted pack URL from.
/// Cloning an operator-pasted URL is a new outbound surface, so it is constrained to an https allowlist
/// (github.com + gitlab.com by default, operator-extensible) — a pasted internal host / non-allowlisted host /
/// non-https URL is refused before any clone runs, closing the SSRF / internal-fetch hole.
/// </summary>
public interface IPackHostAllowlist
{
    /// <summary>True when <paramref name="url"/> is an https URL whose host is on the allowlist.</summary>
    bool IsAllowed(string url);

    /// <summary>Throws <see cref="PackImportException"/> (with an actionable reason) when <paramref name="url"/> is not allowed.</summary>
    void EnsureAllowed(string url);
}
