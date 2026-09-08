using CodeSpace.Core.Settings;
using CodeSpace.Core.Persistence.Entities;
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
    public void Only_nonterminal_capture_states_hold_the_raw_spool_source()
    {
        AgentRunSpoolReaper.CaptureSourceHoldingStates.ShouldBe([
            AgentRunLogCaptureIntentState.Expected,
            AgentRunLogCaptureIntentState.Opened,
            AgentRunLogCaptureIntentState.SourceFinalized,
        ]);
        AgentRunSpoolReaper.CaptureSourceHoldingStates.ShouldNotContain(AgentRunLogCaptureIntentState.Completed);
        AgentRunSpoolReaper.CaptureSourceHoldingStates.ShouldNotContain(AgentRunLogCaptureIntentState.CaptureFailed);
        AgentRunSpoolReaper.CaptureSourceHoldingStates.ShouldNotContain(AgentRunLogCaptureIntentState.Superseded);
        AgentRunSpoolReaper.CaptureSourceHoldingStates.ShouldNotContain(AgentRunLogCaptureIntentState.ExternalStateIndeterminate);
    }

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
    public void RoundSpoolFamily_finds_every_revise_round_sibling_not_just_the_handles_last_round()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-reaper-family-" + Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = root });
        try
        {
            var runId = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(root, runId.ToString("N")));                    // round 0
            Directory.CreateDirectory(Path.Combine(root, $"{runId:N}-r1"));                        // revise round 1
            Directory.CreateDirectory(Path.Combine(root, $"{runId:N}-r2"));                        // revise round 2 (the handle points HERE)
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));           // another run — must not match

            var family = AgentRunSpoolReaper.RoundSpoolFamily(runId);

            family.ShouldContain(Path.Combine(root, runId.ToString("N")), "round 0's spool holds raw output + the config home — it must age out too");
            family.ShouldContain(Path.Combine(root, $"{runId:N}-r1"));
            family.ShouldContain(Path.Combine(root, $"{runId:N}-r2"));
            family.Count.ShouldBe(3, "exactly this run's rounds — never another run's spool");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void IsUnderSpoolRoot_accepts_only_paths_strictly_under_the_spool_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-reaper-guard-" + Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = root });

        AgentRunSpoolReaper.IsUnderSpoolRoot(Path.Combine(root, "abc123")).ShouldBeTrue("a per-run dir under the root");
        AgentRunSpoolReaper.IsUnderSpoolRoot(root).ShouldBeFalse("never the root directory itself");
        AgentRunSpoolReaper.IsUnderSpoolRoot(Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"))).ShouldBeFalse("a sibling outside the root");
        AgentRunSpoolReaper.IsUnderSpoolRoot("/").ShouldBeFalse("never an arbitrary absolute path");
        AgentRunSpoolReaper.IsUnderSpoolRoot(null).ShouldBeFalse();
        AgentRunSpoolReaper.IsUnderSpoolRoot("").ShouldBeFalse();
    }

    [Fact]
    public void Retry_delay_is_stable_monotonic_positive_and_bounded()
    {
        var runId = Guid.NewGuid();
        var delays = Enumerable.Range(1, 100).Select(attempt => AgentRunSpoolReaper.RetryDelay(runId, attempt)).ToArray();

        delays.ShouldAllBe(delay => delay >= TimeSpan.FromMinutes(1) && delay <= TimeSpan.FromHours(6));
        delays.Zip(delays.Skip(1)).ShouldAllBe(pair => pair.First <= pair.Second);
        AgentRunSpoolReaper.RetryDelay(runId, 4).ShouldBe(AgentRunSpoolReaper.RetryDelay(runId, 4), "jitter is derived from durable run identity, not process randomness");
        AgentRunSpoolReaper.RetryDelay(runId, int.MaxValue).ShouldBe(TimeSpan.FromHours(6));
    }
}
