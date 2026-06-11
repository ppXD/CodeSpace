using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The pure, security-relevant logic of the spool reaper: the retention-window env knob (Rule 8 pin + default
/// + fallback) and the containment guard that ensures the reaper can ONLY ever delete a directory strictly
/// under the spool root — never the root itself, never an arbitrary path a corrupt/forged handle might carry.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunSpoolReaperTests
{
    [Fact]
    public void Retention_env_var_name_is_pinned_with_a_24h_default_and_safe_fallback()
    {
        // Renaming this breaks an operator who pinned a custom retention via env — hard-pin it (Rule 8).
        AgentRunSpoolReaper.RetentionEnvVar.ShouldBe("CODESPACE_AGENT_RUN_SPOOL_RETENTION");

        var original = Environment.GetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar, null);
            AgentRunSpoolReaper.Retention.ShouldBe(TimeSpan.FromHours(24), "default retention is 24h");

            Environment.SetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar, "02:00:00");
            AgentRunSpoolReaper.Retention.ShouldBe(TimeSpan.FromHours(2), "a valid TimeSpan override wins");

            Environment.SetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar, "garbage");
            AgentRunSpoolReaper.Retention.ShouldBe(TimeSpan.FromHours(24), "an unparseable value falls back to the default");

            Environment.SetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar, "-01:00:00");
            AgentRunSpoolReaper.Retention.ShouldBe(TimeSpan.FromHours(24), "a non-positive value falls back to the default");
        }
        finally { Environment.SetEnvironmentVariable(AgentRunSpoolReaper.RetentionEnvVar, original); }
    }

    [Fact]
    public void IsUnderSpoolRoot_accepts_only_paths_strictly_under_the_spool_root()
    {
        var original = Environment.GetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar);
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "cs-reaper-guard-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, root);

            AgentRunSpoolReaper.IsUnderSpoolRoot(Path.Combine(root, "abc123")).ShouldBeTrue("a per-run dir under the root");
            AgentRunSpoolReaper.IsUnderSpoolRoot(root).ShouldBeFalse("never the root directory itself");
            AgentRunSpoolReaper.IsUnderSpoolRoot(Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"))).ShouldBeFalse("a sibling outside the root");
            AgentRunSpoolReaper.IsUnderSpoolRoot("/").ShouldBeFalse("never an arbitrary absolute path");
            AgentRunSpoolReaper.IsUnderSpoolRoot(null).ShouldBeFalse();
            AgentRunSpoolReaper.IsUnderSpoolRoot("").ShouldBeFalse();
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, original); }
    }
}
