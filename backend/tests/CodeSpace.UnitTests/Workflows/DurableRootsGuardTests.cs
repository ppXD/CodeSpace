using CodeSpace.Core.Settings;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: P2 slice 3's production /tmp ban — a Production host with unconfigured durable roots (artifact blobs,
/// agent-run spool) refuses to start, naming the exact keys; dev/test keep their temp fallbacks. The exemptions
/// (workspace/pack-clone caches, the MCP socket path, sandbox tmpfs, benchmark workspaces) are documented on the
/// guard itself — ephemeral by design, a decision rather than an oversight.
/// </summary>
[Trait("Category", "Unit")]
public class DurableRootsGuardTests
{
    private static RuntimeSettings Settings(string? artifacts, string? spool) =>
        RuntimeSettings.Current with { ArtifactStoreDirectory = artifacts, AgentRunSpoolDirectory = spool };

    [Fact]
    public void Production_with_both_roots_unconfigured_names_both_keys()
    {
        var violations = DurableRootsGuard.Violations(Settings(null, null), "Production");

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("Artifacts:StoreDirectory"));
        violations.ShouldContain(v => v.Contains("Agents:RunSpoolDirectory"));

        Should.Throw<InvalidOperationException>(() => DurableRootsGuard.ThrowIfProductionUnconfigured(Settings(null, null), "Production"))
            .Message.ShouldContain("Refusing to start in Production");
    }

    [Fact]
    public void Production_with_both_roots_configured_passes()
    {
        DurableRootsGuard.Violations(Settings("/var/lib/codespace/artifacts", "/var/lib/codespace/spool"), "Production").ShouldBeEmpty();
        DurableRootsGuard.ThrowIfProductionUnconfigured(Settings("/a", "/s"), "Production");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Non_production_keeps_the_temp_fallbacks(string environment)
    {
        DurableRootsGuard.Violations(Settings(null, null), environment)
            .ShouldBeEmpty("dev/test temp fallbacks are deliberate — only Production refuses");
    }
}
