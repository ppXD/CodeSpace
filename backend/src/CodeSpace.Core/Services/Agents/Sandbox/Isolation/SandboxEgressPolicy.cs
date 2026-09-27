namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>How a sandboxed run's network egress is bounded.</summary>
public enum SandboxEgressMode
{
    /// <summary>No egress — a fresh net namespace with only loopback (<c>--unshare-net</c>).</summary>
    None,

    /// <summary>Full egress — the host network is shared (today's default when network is allowed without an allowlist).</summary>
    Full,

    /// <summary>Egress filtered to <see cref="SandboxEgressPolicy.AllowedHosts"/> only — the deny-by-default allowlist. The host filtering (privileged netns + nftables / proxy) is enforced by a later sandbox slice.</summary>
    Filtered,
}

/// <summary>
/// The PURE derivation of a sandboxed run's egress posture from its network intent — the B3 (egress allowlist)
/// 底座. It turns the binary <c>AllowNetwork</c> + an optional host allowlist into a single explicit policy, and is
/// FAIL-CLOSED by construction: an allowlist requested on a runner that cannot ENFORCE filtering degrades to
/// <see cref="SandboxEgressMode.None"/> (severed), NEVER to <see cref="SandboxEgressMode.Full"/> — so a narrowing
/// can never silently widen to "any host". No I/O — the actual host filtering is a later slice that reads this.
/// </summary>
public sealed record SandboxEgressPolicy
{
    public required SandboxEgressMode Mode { get; init; }

    /// <summary>The hosts reachable under <see cref="SandboxEgressMode.Filtered"/> — normalized (trimmed, lower-cased, de-duped, blanks dropped). Empty for None / Full.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();

    /// <summary>No egress.</summary>
    public static SandboxEgressPolicy Denied { get; } = new() { Mode = SandboxEgressMode.None };

    /// <summary>Full host-network egress.</summary>
    public static SandboxEgressPolicy Shared { get; } = new() { Mode = SandboxEgressMode.Full };

    /// <summary>
    /// Derive the egress policy: no network ⇒ None; network without an allowlist ⇒ Full (today's behaviour);
    /// network WITH an allowlist ⇒ Filtered when the runner can enforce it, else None (FAIL-CLOSED — never Full).
    /// </summary>
    public static SandboxEgressPolicy Derive(bool allowNetwork, IReadOnlyList<string>? allowlist, bool canEnforceAllowlist)
    {
        if (!allowNetwork) return Denied;

        var hosts = NormalizeHosts(allowlist);

        if (hosts.Count == 0) return Shared;

        return canEnforceAllowlist
            ? new SandboxEgressPolicy { Mode = SandboxEgressMode.Filtered, AllowedHosts = hosts }
            : Denied;
    }

    private static IReadOnlyList<string> NormalizeHosts(IReadOnlyList<string>? hosts) =>
        hosts is null
            ? Array.Empty<string>()
            : hosts.Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
}
