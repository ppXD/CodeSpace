using CodeSpace.Core.Persistence.Entities;
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
    // ── INJECTION / CODE fault → a real MISS the gate MUST red on (the whole point of the fix) ──
    [InlineData("non-zero-exit", "error: unknown option '--append-system-prompt'", false)]   // a malformed persona arg
    [InlineData("non-zero-exit", "error: unexpected argument 'Say hello.' found", false)]     // arg-ordering swallowed the Goal
    [InlineData("executor-error", "AgentOperatingContract.Compose threw: value cannot be null", false)]   // the persona channel threw
    [InlineData("executor-error", "some failure mentioning a 429 in passing", false)]         // executor-error WINS over a gateway-looking word
    [InlineData("non-zero-exit", "claude exited with code 1", false)]                          // an unknown CLI failure defaults to a code fault (never a silent skip)
    public void Classifies_gateway_infra_versus_injection_code_fault(string exitReason, string error, bool expectedInfra)
    {
        var run = new AgentRun { Status = AgentRunStatus.Failed, Error = error, ResultJson = $"{{\"exitReason\":\"{exitReason}\"}}" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBe(expectedInfra,
            customMessage: $"exitReason='{exitReason}', error='{error}' → expected {(expectedInfra ? "GATEWAY INFRA (skip)" : "CODE FAULT (the gate must red)")}");
    }

    [Fact]
    public void A_timed_out_run_is_gateway_infra_regardless_of_message()
    {
        // TimedOut = the model/gateway was too slow — an environmental signal, never a code regression.
        var run = new AgentRun { Status = AgentRunStatus.TimedOut, Error = "the agent run exceeded its time budget" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBeTrue("a time-budget termination is infra, not a code fault");
    }

    [Fact]
    public void ExitReasonOf_reads_the_reason_from_result_json_and_is_empty_when_absent()
    {
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = "{\"exitReason\":\"non-zero-exit\"}" }).ShouldBe("non-zero-exit");
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = null }).ShouldBe("");
        RealModelRunClassifier.ExitReasonOf(new AgentRun { ResultJson = "not json" }).ShouldBe("");
    }
}
