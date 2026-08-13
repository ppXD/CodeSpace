using CodeSpace.Core.Settings;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: P2 slice 3's production /tmp ban — a Production host whose durable roots (artifact blobs, agent-run
/// spool) RESOLVE under the system temp directory refuses to start, naming the key and the path.
///
/// It used to refuse when they were merely UNCONFIGURED, which asked the wrong question and cost a real
/// deployment its boot: the container images already create and own /var/lib/codespace/{artifacts,spool}, so a
/// deployment was being made to restate a decision the image had already taken. DurableRoots supplies those as
/// the default; what remains checked is the only thing that was ever the point.
///
/// The exemptions (workspace/pack-clone caches, the MCP socket path, sandbox tmpfs, benchmark workspaces) are
/// documented on the guard itself — ephemeral by design, a decision rather than an oversight.
/// </summary>
[Trait("Category", "Unit")]
public class DurableRootsGuardTests
{
    private static RuntimeSettings Settings(string? artifacts, string? spool) =>
        RuntimeSettings.Current with { ArtifactStoreDirectory = artifacts, AgentRunSpoolDirectory = spool };

    [Fact]
    public void Production_aimed_at_temp_names_both_keys_and_the_paths()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codespace-probe");

        var violations = DurableRootsGuard.Violations(Settings(temp, temp), "Production");

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains("Artifacts:StoreDirectory") && v.Contains(temp), customMessage: "naming the key without the path leaves the operator guessing which value did it");
        violations.ShouldContain(v => v.Contains("Agents:RunSpoolDirectory") && v.Contains(temp));

        Should.Throw<InvalidOperationException>(() => DurableRootsGuard.ThrowIfProductionUnconfigured(Settings(temp, temp), "Production"))
            .Message.ShouldContain("Refusing to start in Production");
    }

    /// <summary>
    /// The regression that prompted this. An unset root is not a misconfiguration — it means "use the path the
    /// image prepared", and refusing to boot over it is the product getting in its own way.
    /// </summary>
    [Fact]
    public void Production_with_nothing_configured_starts()
    {
        DurableRootsGuard.Violations(Settings(null, null), "Production")
            .ShouldBeEmpty("unconfigured resolves to the container's own /var/lib/codespace paths, or a per-user one off a container — never temp");
    }

    [Fact]
    public void A_sibling_of_the_temp_directory_is_not_inside_it()
    {
        // A prefix compare without the separator would fail /tmpfoo for looking like /tmp.
        var sibling = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + "foo";

        DurableRootsGuard.Violations(Settings(sibling, sibling), "Production").ShouldBeEmpty();
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
    public void Only_production_refuses(string environment)
    {
        DurableRootsGuard.Violations(Settings(Path.GetTempPath(), Path.GetTempPath()), environment)
            .ShouldBeEmpty("a developer deliberately pointing at temp is their business — only Production refuses");
    }
}
