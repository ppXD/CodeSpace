using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
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
    // Every shape here is one a HARNESS actually emits, pinned where the harness parses it — not prose invented here.
    [InlineData("non-zero-exit", "API Error: 401 Authentication Error", true)]                              // ClaudeCodeHarnessTests / CodexHarnessTests: the CLI's own auth announcement
    [InlineData("non-zero-exit", "API Error (429)", true)]                                                  // ClaudeCodeHarnessTests: the `result` line's is_error text
    [InlineData("non-zero-exit", "API Error: Request rejected (429) AccountQuotaExceeded", true)]           // ClaudeCodeHarnessTests: the owner's gateway out of quota
    [InlineData("non-zero-exit", "unexpected status 401 Unauthorized", true)]                               // CodexHarnessTests: Codex's `turn.failed` error.message, lifted verbatim
    [InlineData("non-zero-exit", "API Error: 503 upstream unavailable", true)]                              // a 5xx is Transient in production's own table
    [InlineData("non-zero-exit", "API Error: 404 the responses wire is not served here", true)]             // Codex on a chat/completions-only gateway — an env/wire mismatch
    [InlineData("non-zero-exit", "API Error: Content block is not a thinking block", true)]                 // the gateway mangled the Anthropic wire FORMAT — the live 2026-09-05 text, verbatim
    [InlineData("non-zero-exit", "Anthropic API error (HTTP 429, RateLimited): slow down", true)]           // the ENGINE-written LlmApiException slot, read through RealModelGate
    [InlineData("non-zero-exit", "claude exited with code 1 — stderr: API Error: 429 rate limited", true)]  // the same announcement folded in from stderr
    [InlineData("non-zero-exit", "claude exited with code 1 — stderr: Error: read ECONNRESET", true)]       // an ESTABLISHED connection dropped under us
    // ── INJECTION / CODE fault → a real MISS the gate MUST red on (the whole point of the fix) ──
    [InlineData("non-zero-exit", "error: unknown option '--append-system-prompt'", false)]   // a malformed persona arg
    [InlineData("non-zero-exit", "error: unexpected argument 'Say hello.' found", false)]     // arg-ordering swallowed the Goal
    [InlineData("executor-error", "AgentOperatingContract.Compose threw: value cannot be null", false)]   // the persona channel threw
    [InlineData("executor-error", "some failure mentioning a 429 in passing", false)]         // executor-error WINS over a gateway-looking word
    [InlineData("reattach-error", "API Error: 503 upstream unavailable", false)]              // reattach-error reserves too — WINS even over a genuine gateway marker, same as executor-error above
    [InlineData("non-zero-exit", "claude exited with code 1", false)]                          // an unknown CLI failure defaults to a code fault (never a silent skip)
    [InlineData("non-zero-exit", "API Error: some shape nobody has classified yet", false)]     // the PINNED format marker is infra — a bare "API Error:" prefix is NOT a blanket skip
    // ── The measurement-honesty rows: a GENUINE code fault whose text merely CONTAINS a gateway-looking word. Each of
    //    these skipped the gate under the old substring vocabulary — the exact false-green a stderr fold makes routine.
    [InlineData("non-zero-exit", "the route returned 404 as asserted, but the body was empty", false)]
    [InlineData("non-zero-exit", "connection string missing for the test database", false)]
    [InlineData("non-zero-exit", "refused to overwrite the existing file", false)]
    [InlineData("non-zero-exit", "assertion failed: expected 503 Service Unavailable, got 200", false)]
    [InlineData("non-zero-exit", "the API Error handler did not fire", false)]                  // the announcement phrase with NO status announces nothing
    [InlineData("non-zero-exit", "the api error path returned 500 in the fixture", false)]      // prose ABOUT an announcement is not one — the phrase is matched in the harness's own casing
    [InlineData("non-zero-exit", "the gateway quota check is unauthorized for this rate limit", false)]   // five old substrings at once, and still just prose
    [InlineData("non-zero-exit", "claude exited with code 1 — stderr: TimeoutException waiting for the fake CLI", false)]   // a genuinely slow gateway arrives as Status=TimedOut, not as prose
    // ── Free-text shapes that LOOK like a real gateway signal but match no anchor yet — deliberately GENUINE, not a
    //    gap in this PR: each is a candidate marker with no production anchor to read it from today, so it stays a
    //    code fault (conservative) until a live lane observation pins the actual format, exactly as GatewayFormatFault
    //    was pinned from the 2026-08-30 wedge. A future PR may promote any of these once that evidence exists.
    [InlineData("non-zero-exit", "overloaded_error: the model is temporarily overloaded, please retry", false)]  // Anthropic's real error TYPE — no anchor reads it yet
    [InlineData("non-zero-exit", "request timed out after 150s", false)]                       // prose timeout, not the libc ETIMEDOUT code DroppedTransportRegex reads
    [InlineData("non-zero-exit", "503 service unavailable", false)]                            // no "API Error"/"unexpected status" phrase anchors the status
    [InlineData("non-zero-exit", "API error: 401 unauthorized", false)]                         // lowercase "error" — not the harness's own casing the anchor matches
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

    [Theory]
    [InlineData(429, LlmErrorCategory.RateLimited, true)]
    [InlineData(503, LlmErrorCategory.Transient, true)]
    [InlineData(408, LlmErrorCategory.Transient, true)]
    [InlineData(401, LlmErrorCategory.AuthFailed, true)]
    [InlineData(400, LlmErrorCategory.BadRequest, false)]     // a request the gateway understood and rejected is OUR shape to fix — a fault, not weather
    [InlineData(422, LlmErrorCategory.BadRequest, false)]
    public void An_announced_status_is_graded_by_productions_own_table(int status, LlmErrorCategory expectedCategory, bool expectedInfra)
    {
        // The status is READ from the harness's own announcement slot, then handed to the SAME function the LLM
        // transport classifies its own failures with. Nothing here re-decides what a status means: a change to
        // production's table moves this gate with it, and the gate can never bless a status production calls a
        // request fault. The categories that skip are exactly the ones RealModelGate already propagates as infra.
        LlmApiException.Classify(status, body: null).ShouldBe(expectedCategory,
            customMessage: "production owns status → category; if this moved, the gate's arm moves with it rather than growing a second table");

        var run = new AgentRun { Status = AgentRunStatus.Failed, Error = $"API Error: {status} whatever the gateway said", ResultJson = "{\"exitReason\":\"non-zero-exit\"}" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBe(expectedInfra,
            customMessage: $"an announced HTTP {status} is {expectedCategory} → expected {(expectedInfra ? "INFRA (skip)" : "CODE FAULT (red)")}");
    }

    [Theory]
    // ESTABLISHED-then-dropped: the connection existed and died under us — weather.
    [InlineData("Error: read ECONNRESET", true)]
    [InlineData("Error: write EPIPE", true)]
    [InlineData("Error: connect ETIMEDOUT 10.0.0.4:443", true)]
    // CONNECT / DNS: the endpoint was never reachable — a mis-pointed base URL is a WIRING bug the gate must CATCH,
    // exactly as RealModelGate.WiringSocketErrors refuses the same SocketErrors on the exception path.
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:5432", false)]
    [InlineData("Error: getaddrinfo ENOTFOUND gateway.invalid", false)]
    // The English words those codes used to be matched by — prose from a genuine code fault, and no longer a skip.
    [InlineData("the connection was refused by the fixture, as the test expects", false)]
    public void Only_an_established_connection_that_dropped_is_transport_infra(string error, bool expectedInfra)
    {
        var run = new AgentRun { Status = AgentRunStatus.Failed, Error = error, ResultJson = "{\"exitReason\":\"non-zero-exit\"}" };

        RealModelRunClassifier.IsGatewayInfra(run).ShouldBe(expectedInfra,
            customMessage: $"'{error}' → expected {(expectedInfra ? "INFRA (skip)" : "CODE FAULT (red)")}; the split mirrors RealModelGate.WiringSocketErrors on the exception path");
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
