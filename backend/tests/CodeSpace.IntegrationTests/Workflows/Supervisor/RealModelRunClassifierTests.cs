using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Deterministic proof (no live model) that <see cref="RealModelRunClassifier"/> makes the behavioral injection GATE
/// SOUND: a real injection/code regression is classified as a MISS the gate reds on, while a genuine gateway hiccup is a
/// non-gating skip. This is the guard the adversarial review demanded — without it the persona gate could not red on the
/// exact regression class it exists to catch (every non-Succeeded status collapsed to a silent infra skip).
/// </summary>
public sealed class RealModelRunClassifierTests
{
    [Theory]
    // ── GATEWAY / transport / auth / rate → infra skip (must NOT red the gate on the owner's slow/down gateway) ──
    [InlineData("non-zero-exit", "API error: 401 unauthorized (invalid x-api-key)", true)]
    [InlineData("non-zero-exit", "Error: 429 Too Many Requests", true)]
    [InlineData("non-zero-exit", "overloaded_error: the model is overloaded", true)]
    [InlineData("non-zero-exit", "connection refused (local:443)", true)]
    [InlineData("non-zero-exit", "request timed out after 150s", true)]
    [InlineData("non-zero-exit", "503 service unavailable", true)]
    [InlineData("non-zero-exit", "API Error: Content block is not a thinking block", true)]   // the gateway mangled the Anthropic wire FORMAT — the live 2026-09-05 text, verbatim
    // ── INJECTION / CODE fault → a real MISS the gate MUST red on (the whole point of the fix) ──
    [InlineData("non-zero-exit", "error: unknown option '--append-system-prompt'", false)]   // a malformed persona arg
    [InlineData("non-zero-exit", "error: unexpected argument 'Say hello.' found", false)]     // arg-ordering swallowed the Goal
    [InlineData("executor-error", "AgentOperatingContract.Compose threw: value cannot be null", false)]   // the persona channel threw
    [InlineData("executor-error", "some failure mentioning a 429 in passing", false)]         // executor-error WINS over a gateway-looking word
    [InlineData("non-zero-exit", "claude exited with code 1", false)]                          // an unknown CLI failure defaults to a code fault (never a silent skip)
    [InlineData("non-zero-exit", "API Error: some shape nobody has classified yet", false)]     // the PINNED format marker is infra — a bare "API Error:" prefix is NOT a blanket skip
    public void Classifies_gateway_infra_versus_injection_code_fault(string exitReason, string error, bool expectedInfra)
    {
        var run = new AgentRun { Status = AgentRunStatus.Failed, Error = error, ResultJson = $"{{\"exitReason\":\"{exitReason}\"}}" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBe(expectedInfra,
            customMessage: $"exitReason='{exitReason}', error='{error}' → expected {(expectedInfra ? "GATEWAY INFRA (skip)" : "CODE FAULT (the gate must red)")}");
    }

    [Fact]
    public void The_gateway_format_fault_vocabulary_is_read_from_production_not_copied()
    {
        // The live gateway wedge kills the agent tail with exactly this text, on a CLI version pinned since July.
        // Production owns the marker (it is the cause its retry path degrades against); the gate MUST reach the same
        // verdict, or one lane skips the exit honestly while another reds it as a "publish-guard regression" — the
        // split-brain this test exists to prevent. Reading the vocabulary from production is what holds them in
        // lockstep: a marker added there is honoured here with no edit, so the two can never drift apart again.
        const string liveGatewayText = "API Error: Content block is not a thinking block";

        AgentRetryCauses.Classify(liveGatewayText).ShouldBe(AgentRetryCauses.GatewayFormatFault,
            customMessage: "production owns the format-fault marker vocabulary — if this moved, update the gate's delegation, never a second copy of the string");

        var run = new AgentRun { Status = AgentRunStatus.Failed, Error = liveGatewayText, ResultJson = "{\"exitReason\":\"non-zero-exit\"}" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBeTrue(
            customMessage: "the real-model gate must read the gateway's mangled wire as INFRA (non-gating skip), never as a code regression");
    }

    [Fact]
    public void A_run_that_completed_is_never_diverted_to_an_infra_skip()
    {
        // The other half of the contract: the infra path only ever opens for a run that did NOT complete. A run that
        // COMPLETED and then violated the repository's publish policy is a GENUINE regression the gate must red on —
        // widening the infra vocabulary must never buy a completed-but-misbehaving run a skip.
        var completed = new AgentRun { Status = AgentRunStatus.Succeeded, ResultJson = "{\"exitReason\":\"\"}" };

        RealModelRunClassifier.HasInspectableModelReply(completed).ShouldBeTrue(
            customMessage: "a Succeeded run carries an inspectable reply, so the lane proceeds to its publish-policy assertions instead of consulting the infra classifier");
    }

    [Fact]
    public void A_timed_out_run_is_gateway_infra_regardless_of_message()
    {
        // TimedOut = the model/gateway was too slow — an environmental signal, never a code regression.
        var run = new AgentRun { Status = AgentRunStatus.TimedOut, Error = "the agent run exceeded its time budget" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBeTrue("a time-budget termination is infra, not a code fault");
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, "", true)]
    [InlineData(AgentRunStatus.NeedsReview, "needs-review", true)]
    [InlineData(AgentRunStatus.NeedsReview, "output-flagged", false)]
    [InlineData(AgentRunStatus.NeedsReview, "stalled", false)]
    [InlineData(AgentRunStatus.NeedsReview, "needs-decision", false)]
    [InlineData(AgentRunStatus.Failed, "non-zero-exit", false)]
    [InlineData(AgentRunStatus.Cancelled, "cancelled", false)]
    [InlineData(AgentRunStatus.TimedOut, "timed-out", false)]
    [InlineData(AgentRunStatus.Running, "", false)]
    public void Only_output_bearing_terminal_runs_are_inspectable_by_behavioral_gates(AgentRunStatus status, string exitReason, bool expected)
    {
        var run = new AgentRun { Status = status, ResultJson = $"{{\"exitReason\":\"{exitReason}\"}}" };

        RealModelRunClassifier.HasInspectableModelReply(run).ShouldBe(expected);
    }

    [Fact]
    public void ExitReasonOf_reads_the_reason_from_result_json_and_is_empty_when_absent()
    {
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = "{\"exitReason\":\"non-zero-exit\"}" }).ShouldBe("non-zero-exit");
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = null }).ShouldBe("");
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = "not json" }).ShouldBe("");
    }
}
