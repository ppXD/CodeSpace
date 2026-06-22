using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the pure egress-policy derivation (B3 底座) — the FAIL-CLOSED rule that turns AllowNetwork + an optional
/// host allowlist into one explicit posture. The load-bearing safety property: an allowlist requested on a runner
/// that cannot ENFORCE filtering degrades to None (severed), NEVER to Full — a narrowing can never silently widen.
/// </summary>
[Trait("Category", "Unit")]
public class SandboxEgressPolicyTests
{
    [Fact]
    public void No_network_is_None_regardless_of_an_allowlist()
    {
        SandboxEgressPolicy.Derive(allowNetwork: false, allowlist: null, canEnforceAllowlist: true).Mode.ShouldBe(SandboxEgressMode.None);
        SandboxEgressPolicy.Derive(allowNetwork: false, allowlist: new[] { "api.anthropic.com" }, canEnforceAllowlist: true).Mode.ShouldBe(SandboxEgressMode.None);
    }

    [Fact]
    public void Network_without_an_allowlist_is_Full_todays_behaviour()
    {
        SandboxEgressPolicy.Derive(allowNetwork: true, allowlist: null, canEnforceAllowlist: false).Mode.ShouldBe(SandboxEgressMode.Full);
        SandboxEgressPolicy.Derive(allowNetwork: true, allowlist: Array.Empty<string>(), canEnforceAllowlist: true).Mode.ShouldBe(SandboxEgressMode.Full);
    }

    [Fact]
    public void An_allowlist_fails_closed_to_None_when_it_cannot_be_enforced()
    {
        // The B3.1 reality: enforcement isn't built yet → an allowlist denies all egress, never falls open to Full.
        var policy = SandboxEgressPolicy.Derive(allowNetwork: true, allowlist: new[] { "api.anthropic.com" }, canEnforceAllowlist: false);

        policy.Mode.ShouldBe(SandboxEgressMode.None);
        policy.AllowedHosts.ShouldBeEmpty();
    }

    [Fact]
    public void An_allowlist_of_only_blanks_normalizes_to_empty_and_reopens_to_Full_not_fail_closed()
    {
        // A NON-empty input that normalizes away to nothing is effectively "no allowlist" → Full, NOT fail-closed
        // None (and never Filtered-with-no-hosts). Pins the boundary so a future NormalizeHosts refactor can't regress it.
        var policy = SandboxEgressPolicy.Derive(allowNetwork: true, allowlist: new[] { "", "   ", "\t" }, canEnforceAllowlist: true);

        policy.Mode.ShouldBe(SandboxEgressMode.Full, "a blank-only allowlist is no allowlist → reopen to Full");
        policy.AllowedHosts.ShouldBeEmpty();
    }

    [Fact]
    public void An_enforceable_allowlist_is_Filtered_with_the_normalized_hosts()
    {
        var policy = SandboxEgressPolicy.Derive(
            allowNetwork: true,
            allowlist: new[] { "  API.Anthropic.com ", "github.com", "api.anthropic.com", "  ", "" },
            canEnforceAllowlist: true);

        policy.Mode.ShouldBe(SandboxEgressMode.Filtered);
        policy.AllowedHosts.ShouldBe(new[] { "api.anthropic.com", "github.com" }, "trimmed, lower-cased, de-duped, blanks dropped");
    }
}
