using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void The_shared_mitigation_owns_both_halves_of_the_repair()
    {
        // The helper is the ONE place "what a format-fault retry runs as" is written. Both halves are load-bearing:
        // a fresh conversation (a resume re-sends the transcript the mangled block lives in) AND thinking disabled.
        var warm = Task_() with { ResumeFromSessionId = "sess-1", RestoredTranscript = "transcript", RestoredTranscriptArtifactId = Guid.NewGuid() };

        var mitigated = AgentRetryCauses.ApplyFormatFaultMitigation(warm);

        mitigated.ResumeFromSessionId.ShouldBeNull();
        mitigated.RestoredTranscript.ShouldBeNull();
        mitigated.RestoredTranscriptArtifactId.ShouldBeNull();
        mitigated.Environment["KEEP"].ShouldBe("kept", "the degrade adds one variable — it never rebuilds the environment from scratch");
        AgentRetryCauses.IsFormatFaultMitigated(mitigated).ShouldBeTrue();
        AgentRetryCauses.IsFormatFaultMitigated(warm).ShouldBeFalse("the predicate reads the degrade, not the intent — an un-mitigated task must never look repaired");
    }

    [Fact]
    public void A_task_whose_persisted_environment_is_null_is_not_mitigated()
    {
        // The predicate reads the DURABLE envelope — task_jsonb on every claim, and the engine's own suspend payload —
        // and System.Text.Json writes a literal `null` straight onto the property, bypassing the record's default
        // initializer (that runs only when the key is ABSENT). An unguarded dereference would kill the dispatch with an
        // NRE before the harness ever starts, on a task whose only sin is carrying no environment at all.
        var task = JsonSerializer.Deserialize<AgentTask>("""{"goal":"fix it","harness":"claude-code","environment":null}""", AgentJson.Options)!;

        AgentRetryCauses.IsFormatFaultMitigated(task).ShouldBeFalse("no environment carries no degrade — the answer is 'not mitigated', never a throw");
    }

    [Fact]
    public void The_mitigation_note_describes_this_attempt_and_counts_nothing()
    {
        // The note is emitted from the dispatch of EVERY mitigated task, and the supervisor lane re-applies the
        // mitigation on each format-fault retry (RealSupervisorActionExecutor.ApplyRetryDisposition) — so several of
        // these can stand on one run's timeline. A note that says "once" would then be a claim the run's own history
        // refutes, and would read as a promise about the budget the dispatcher cannot see. It reports THIS attempt's
        // shape and nothing else; pinned here because the wording is the whole of what it delivers.
        AgentRunExecutor.FormatFaultMitigationNote.ShouldBe(
            "Gateway format fault — respawned with thinking disabled (fresh conversation: the mangled block lives in the prior transcript).");
    }

    [Fact]
    public async Task Both_retry_lanes_repair_a_format_fault_through_the_same_helper()
    {
        // Drift detector (Rule 12.5, behavioural form): the supervisor's `retry` and agent.run's respawn resolve
        // their prior attempt from different sources — a DB-loaded ResumableSession vs. a flat resume payload — but
        // the repair they apply must be the ONE the helper owns. A lane that copies the literal instead passes today
        // and silently un-repairs the moment the helper changes (a renamed env var, a third half added), so both
        // lanes are asserted through the helper's own predicate, never through a re-typed "MAX_THINKING_TOKENS".
        const string liveError = "API Error: Content block is not a thinking block";

        var supervisorTask = RealSupervisorActionExecutor.ApplyRetryDisposition(
            Task_(), new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null), Result(liveError), workspaceHasPriorWork: true);

        var priorAttempt = JsonDocument.Parse($$"""
            {"status":"Failed","exitReason":"non-zero-exit","error":"{{liveError}}","sessionId":"sess-1","sessionTranscript":"transcript"}
            """).RootElement;

        var node = await new AgentCodeNode().RunAsync(NodeContext(priorAttempt), CancellationToken.None);
        var nodeTask = JsonSerializer.Deserialize<AgentTask>(node.SuspendUntil!.Payload, AgentJson.Options)!;

        foreach (var (lane, task) in new[] { ("supervisor retry", supervisorTask), ("agent.run respawn", nodeTask) })
        {
            AgentRetryCauses.IsFormatFaultMitigated(task).ShouldBeTrue($"the {lane} lane must apply the SHARED mitigation, not its own copy of it");
            task.ResumeFromSessionId.ShouldBeNull($"the {lane} lane must start FRESH — a replay re-triggers the fault deterministically");
            task.RestoredTranscript.ShouldBeNull($"the {lane} lane must not carry the poisoned transcript forward");
        }
    }

    private static NodeRunContext NodeContext(JsonElement priorAttemptPayload) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = new Dictionary<string, JsonElement>
        {
            ["goal"] = JsonSerializer.SerializeToElement("fix it"),
            ["harness"] = JsonSerializer.SerializeToElement("claude-code"),
        },
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        PriorAttemptPayload = priorAttemptPayload,
    };

    private static AgentTask Task_() => new() { Goal = "fix it", Harness = "claude-code", Environment = new Dictionary<string, string> { ["KEEP"] = "kept" } };

    private static SupervisorAgentResult Result(string error) => new() { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = error };
}
