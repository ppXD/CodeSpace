using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: cause-aware retry — the 2026-08-30 wedge's L3 layer. Pins: the LIVE error text classifies as a gateway
/// format fault (and unrelated/absent errors classify as nothing — the marker vocabulary is tight, an over-broad
/// match would strip resume continuity from ordinary failures); a format-fault retry goes FRESH with extended
/// thinking disabled while every other shape keeps today's resume/cold-start semantics byte-identically; the env
/// var name is pinned (Rule 8 — a rename would silently un-degrade every format-fault retry).
/// </summary>
[Trait("Category", "Unit")]
public class AgentRetryCausesTests
{
    [Theory]
    [InlineData("API Error: Content block is not a thinking block", AgentRetryCauses.GatewayFormatFault)]   // the live 2026-08-30 text, verbatim
    [InlineData("acceptance: ./check.sh exited 2", null)]
    [InlineData("Anthropic API error (HTTP 429, RateLimited)", null)]   // transient — resume is still the right default
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_pinned_format_fault_markers_classify(string? error, string? expected)
    {
        AgentRetryCauses.Classify(error).ShouldBe(expected);
    }

    [Fact]
    public void The_thinking_env_var_name_is_pinned()
    {
        // The claude CLI reads exactly this name; renaming the constant would silently un-degrade every
        // format-fault retry while the tests stay green. Hard-pin (Rule 8).
        AgentRetryCauses.MaxThinkingTokensEnvVar.ShouldBe("MAX_THINKING_TOKENS");
        AgentRetryCauses.WithThinkingDisabled(new Dictionary<string, string>())["MAX_THINKING_TOKENS"].ShouldBe("0");
    }

    [Fact]
    public void A_format_fault_retry_goes_fresh_with_thinking_disabled()
    {
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);
        var result = Result("API Error: Content block is not a thinking block");

        var task = RealSupervisorActionExecutor.ApplyRetryDisposition(Task_(), prior, result, workspaceHasPriorWork: true);

        task.ResumeFromSessionId.ShouldBeNull("a conversation replay re-triggers the format fault deterministically — the retry must NOT resume");
        task.RestoredTranscript.ShouldBeNull();
        task.Environment[AgentRetryCauses.MaxThinkingTokensEnvVar].ShouldBe("0", "…and runs with extended thinking disabled, the shape the gateway cannot mangle");
        task.Environment["KEEP"].ShouldBe("kept", "the degrade adds one variable — it never rebuilds the environment from scratch");
    }

    [Fact]
    public void An_ordinary_failure_keeps_todays_resume_semantics_byte_identically()
    {
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);

        var resumed = RealSupervisorActionExecutor.ApplyRetryDisposition(Task_(), prior, Result("acceptance: ./check.sh exited 2"), workspaceHasPriorWork: true);
        resumed.ResumeFromSessionId.ShouldBe("sess-1");
        resumed.Environment.ContainsKey(AgentRetryCauses.MaxThinkingTokensEnvVar).ShouldBeFalse("no degrade on an ordinary failure");

        var cold = RealSupervisorActionExecutor.ApplyRetryDisposition(Task_(), prior: null, Result("boom"), workspaceHasPriorWork: false);
        cold.ResumeFromSessionId.ShouldBeNull();
        cold.Environment.ContainsKey(AgentRetryCauses.MaxThinkingTokensEnvVar).ShouldBeFalse();
    }

    private static AgentTask Task_() => new() { Goal = "fix it", Harness = "claude-code", Environment = new Dictionary<string, string> { ["KEEP"] = "kept" } };

    private static SupervisorAgentResult Result(string error) => new() { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = error };
}
