using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Pins the real-model gate POLICY (which wires gate CI) + the informational reporting promise. Everything is driven
/// through the gate's PURE seams (the raw-string overload of <see cref="RealModelGate.IsRequired(string,string?)"/> and
/// <see cref="RealModelGate.ReportInformational"/> with an explicit path), so these tests never mutate process-wide env
/// — there is no global state to race a concurrent reader.
/// </summary>
public sealed class RealModelGateTests
{
    [Fact]
    public void Gate_policy_env_var_names_are_pinned()
    {
        // Renaming either breaks an operator who pinned the blessed wire set / relies on the CI summary channel.
        RealModelGate.RequiredProvidersEnvVar.ShouldBe("CODESPACE_REALMODEL_REQUIRED_PROVIDERS");
        RealModelGate.StepSummaryEnvVar.ShouldBe("GITHUB_STEP_SUMMARY");
    }

    [Theory]
    [InlineData(null)]            // unset → default blessed set
    [InlineData("")]             // blank → default
    [InlineData("   ")]          // whitespace → default
    [InlineData(" , ")]          // all-blank entries → default (never blesses nobody)
    public void By_default_Anthropic_gates_and_OpenAI_is_informational(string? rawOverride)
    {
        RealModelGate.IsRequired("Anthropic", rawOverride).ShouldBeTrue("Anthropic is the default blessed wire");
        RealModelGate.IsRequired("anthropic", rawOverride).ShouldBeTrue("provider match is case-insensitive");
        RealModelGate.IsRequired("OpenAI", rawOverride).ShouldBeFalse("OpenAI is informational by default — its verdict must not gate CI");
    }

    [Fact]
    public void An_operator_can_rebless_the_wires_via_the_override_string()
    {
        RealModelGate.IsRequired("OpenAI", "OpenAI, Anthropic").ShouldBeTrue("the override blesses OpenAI too (and tolerates spaces)");
        RealModelGate.IsRequired("Anthropic", "OpenAI, Anthropic").ShouldBeTrue();
        RealModelGate.IsRequired("Anthropic", "OpenAI").ShouldBeFalse("an override that omits Anthropic un-blesses it");
    }

    [Fact]
    public void A_required_wires_bad_verdict_fails_the_job_but_reporting_an_informational_one_never_throws()
    {
        // The blessed wire (Anthropic by default) THROWS on a bad verdict — that is what fails the CI job. (The
        // required path writes no step summary, so this asserts cleanly without polluting the real CI job summary.)
        Should.Throw<Shouldly.ShouldAssertException>(() => RealModelGate.Assess("Anthropic", ok: false, verdict: "bad"));

        // Reporting an informational wire's bad verdict NEVER throws — it cannot gate CI (tested via the pure seam so
        // it neither reads nor writes the real GITHUB_STEP_SUMMARY).
        Should.NotThrow(() => RealModelGate.ReportInformational(ok: false, verdict: "bad", stepSummaryPath: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_informational_verdict_is_appended_to_the_step_summary_file(bool ok)
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-summary-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportInformational(ok, $"OpenAI trajectory — {(ok ? "drove to completion" : "never stopped")}", path);

            var written = File.ReadAllText(path);
            written.ShouldContain("INFORMATIONAL");
            written.ShouldContain(ok ? "drove to completion" : "never stopped");
            written.ShouldContain("NOT gating");   // the report states plainly it does not gate
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Transient_transport_failures_are_infra_but_wiring_failures_and_logic_errors_gate()
    {
        // TRANSIENT (slow / dropped gateway) → non-gating infra:
        RealModelGate.IsGatewayInfraFailure(new TaskCanceledException("timeout", new TimeoutException())).ShouldBeTrue("an HttpClient.Timeout is the gateway being slow");
        RealModelGate.IsGatewayInfraFailure(new TimeoutException()).ShouldBeTrue();
        RealModelGate.IsGatewayInfraFailure(new System.IO.IOException("response stream ended")).ShouldBeTrue("a mid-stream drop (incl. HttpIOException) is transient transport");
        RealModelGate.IsGatewayInfraFailure(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)).ShouldBeTrue("an established-then-reset connection is transient");
        // Flattened through an AggregateException (a future parallel drive) — the TimeoutException in a non-first slot is still found.
        RealModelGate.IsGatewayInfraFailure(new AggregateException(new InvalidOperationException("x"), new TimeoutException())).ShouldBeTrue("an aggregate-wrapped timeout is still infra");

        // WIRING (mis-pointed/unreachable endpoint) → MUST gate (a broken wire can't green the kill-gate):
        RealModelGate.IsGatewayInfraFailure(new System.Net.Http.HttpRequestException("name not resolved")).ShouldBeFalse("a bare HttpRequestException (DNS/connect) is a wiring failure, not transient");
        RealModelGate.IsGatewayInfraFailure(new System.Net.Http.HttpRequestException("dns", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound))).ShouldBeFalse("an unresolvable host is a wiring failure");
        RealModelGate.IsGatewayInfraFailure(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)).ShouldBeFalse("a refused connection is a mis-pointed endpoint, a wiring failure");

        // Our OWN deadline cancellation carries no TimeoutException → NOT infra (a "did not converge" verdict must gate).
        RealModelGate.IsGatewayInfraFailure(new OperationCanceledException()).ShouldBeFalse("a bare cancel is our deadline, not the gateway");
        RealModelGate.IsGatewayInfraFailure(new TaskCanceledException()).ShouldBeFalse();
        // A real logic bug / assertion must NEVER be misread as infra.
        RealModelGate.IsGatewayInfraFailure(new InvalidOperationException("wiring bug")).ShouldBeFalse();
        RealModelGate.IsGatewayInfraFailure(new Shouldly.ShouldAssertException("scored 3/5")).ShouldBeFalse();
    }

    [Fact]
    public async Task AssessLiveAsync_treats_a_gateway_timeout_as_non_gating_even_for_the_blessed_wire()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-infra-{Guid.NewGuid():N}.md");
        try
        {
            // Anthropic is the blessed wire; a gateway timeout must NOT fail the job, AND must be surfaced loudly.
            await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("Anthropic",
                () => throw new TaskCanceledException("timeout", new TimeoutException()), gating: true, path));

            var written = File.ReadAllText(path);
            written.ShouldContain("NON-GATING infra skip");
            written.ShouldContain("Anthropic");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AssessLiveAsync_still_gates_the_blessed_wire_on_a_genuine_bad_verdict_and_passes_a_good_one()
    {
        // A clean completion with ok=false on the blessed wire FAILS the job — the gate's teeth are intact. (Caught via
        // a plain try/catch because the async Should.ThrowAsync does not reliably catch Shouldly's own assertion type.)
        var gated = false;
        try { await RealModelGate.AssessLiveAsync("Anthropic", () => Task.FromResult((false, "scored 3/5")), gating: true, stepSummaryPath: null); }
        catch (Shouldly.ShouldAssertException) { gated = true; }
        gated.ShouldBeTrue("a blessed wire's genuine bad verdict must fail the job");

        // ok=true passes cleanly.
        await Should.NotThrowAsync(() =>
            RealModelGate.AssessLiveAsync("Anthropic", () => Task.FromResult((true, "scored 5/5")), gating: true, stepSummaryPath: null));
    }

    [Fact]
    public async Task AssessLiveAsync_with_gating_false_reports_a_bad_verdict_informationally_and_never_fails_the_job()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-info-{Guid.NewGuid():N}.md");
        try
        {
            // A demoted (informational) lane on the BLESSED wire must NOT fail the job even on a bad verdict — its result
            // is observed (a precondition the blessed decision-eval already measures), not a kill-gate. It is still REPORTED.
            await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("Anthropic",
                () => Task.FromResult((false, "whole-loop: no conformant decision")), gating: false, path));

            var written = File.ReadAllText(path);
            written.ShouldContain("INFORMATIONAL");
            written.ShouldContain("NOT gating");
            written.ShouldContain("no conformant decision");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AssessLiveAsync_never_swallows_a_non_infra_exception()
    {
        // A real bug in the drive (not a gateway failure) must PROPAGATE, never be masked as an infra skip.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            RealModelGate.AssessLiveAsync("Anthropic", () => throw new InvalidOperationException("wiring bug"), gating: true, stepSummaryPath: null));
    }

    // ── Three-way whole-loop gate: the blessed wire reds ONLY on a code regression, never on a model-capability miss ──

    [Fact]
    public async Task The_three_way_gate_fails_the_blessed_wire_ONLY_on_a_code_fault()
    {
        // CodeFault on the blessed wire FAILS the job — the engine crashed driving the live brain's (valid) decisions.
        // (Plain try/catch because async Should.ThrowAsync does not reliably catch Shouldly's own assertion type.)
        var gated = false;
        try { await RealModelGate.AssessLiveAsync("Anthropic", () => Task.FromResult((RealModelOutcome.CodeFault, "engine threw mid-merge")), stepSummaryPath: null); }
        catch (Shouldly.ShouldAssertException) { gated = true; }
        gated.ShouldBeTrue("a CodeFault on the blessed wire must fail the job — a real code regression");

        // CapabilityMiss on the blessed wire is REPORTED, never gates — the gateway model couldn't drive, not a code bug.
        await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("Anthropic", () => Task.FromResult((RealModelOutcome.CapabilityMiss, "no conformant decision")), stepSummaryPath: null));

        // Drove passes cleanly.
        await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("Anthropic", () => Task.FromResult((RealModelOutcome.Drove, "plan→spawn→merge→accept")), stepSummaryPath: null));

        // An informational wire never gates — not even on a CodeFault.
        await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("OpenAI", () => Task.FromResult((RealModelOutcome.CodeFault, "engine threw")), stepSummaryPath: null));
    }

    [Fact]
    public async Task The_three_way_gate_treats_a_gateway_timeout_as_non_gating_even_on_the_blessed_wire()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-3way-infra-{Guid.NewGuid():N}.md");
        try
        {
            await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("Anthropic",
                () => throw new TaskCanceledException("timeout", new TimeoutException()), path));

            var written = File.ReadAllText(path);
            written.ShouldContain("NON-GATING infra skip");
            written.ShouldContain("Anthropic");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_three_way_gate_never_swallows_a_non_infra_exception()
    {
        // A real bug while driving (not a gateway failure) PROPAGATES, never masked as a skip — same teeth as the boolean overload.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            RealModelGate.AssessLiveAsync("Anthropic", () => throw new InvalidOperationException("harness bug"), stepSummaryPath: null));
    }

    [Fact]
    public void A_gateway_infra_node_failure_is_recognised_but_a_real_engine_fault_and_a_model_capability_miss_are_not()
    {
        // A mid-turn GATEWAY/credential outage that the engine swallowed into a run Failure carries the typed
        // LlmApiException signature of an INFRA category (Transient / RateLimited / AuthFailed) in the ENGINE-WRITTEN
        // "(status, category): " slot at the START of the node-failed record's `error` field → recognised as NON-GATING
        // infra (honours the lane-wide "a gateway outage never gates" guarantee). Both the real JSON payload form
        // ({"error":"…"}) and a raw error string are accepted.
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (no-status, Transient): the request timed out before the gateway responded")).ShouldBeTrue("a transient gateway timeout is infra");
        RealModelGate.IsGatewayInfraError("OpenAI API error (HTTP 503, Transient): upstream unavailable").ShouldBeTrue("a 5xx is a transient gateway fault (raw error form)");
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (HTTP 429, RateLimited): slow down")).ShouldBeTrue("a 429 is a rate-limited gateway fault");
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (HTTP 401, AuthFailed): invalid key")).ShouldBeTrue("a rotated/revoked credential is a credential-infra outage, not a code regression — it must not gate main");

        // A GENUINE engine/decision fault must NOT be mis-skipped — it has to gate (a real regression). None carry the
        // typed infra-category signature in the leading slot.
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Node 'sup' failed.")).ShouldBeFalse("the generic run-level error is not an infra signal");
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("System.NullReferenceException: object reference not set")).ShouldBeFalse("a null-ref is a real code fault");
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("git merge failed: conflict in shared.txt")).ShouldBeFalse("a git fault gates");

        // A model-CAPABILITY miss is handled at the decider (fail-closed to a clean stop) so it never reaches a run
        // Failure — and is NOT an infra category here either (it would be a CapabilityMiss, never a gate).
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (HTTP 400, BadRequest): unsupported")).ShouldBeFalse("a bad-request is a model-capability category, not infra");
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (no-status, Malformed): structured output failed schema validation after a re-ask")).ShouldBeFalse("a schema-invalid reply is a capability miss the decider fail-closes, not infra");
        RealModelGate.IsGatewayInfraError(null).ShouldBeFalse();
        RealModelGate.IsGatewayInfraError("").ShouldBeFalse();
    }

    [Fact]
    public void A_non_transient_fault_whose_BODY_text_contains_a_fake_infra_slot_is_NOT_mis_skipped()
    {
        // THE attack the anchored slot defends: providerMessage is untrusted upstream body text (the raw error body for a
        // non-2xx, an HttpRequestException.Message for a transport drop). A NON-transient fault whose body merely CONTAINS
        // the literal ", Transient): " — or a whole fake "API error (x, Transient): " — must NOT route to the non-gating
        // infra-skip (the prior unanchored substring check could). The category is read ONLY from the engine-written
        // leading slot, where the real category (BadRequest / AuthFailed-is-infra-but-here-BadRequest) sits.
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("Anthropic API error (HTTP 400, BadRequest): the upstream body said \"retry, Transient): later\""))
            .ShouldBeFalse("the ', Transient): ' is in the untrusted body, not the engine-written leading slot — it must still gate");

        RealModelGate.IsGatewayInfraError(NodeFailedPayload("OpenAI API error (HTTP 400, BadRequest): nested API error (x, Transient): boom"))
            .ShouldBeFalse("a whole fake 'API error (x, Transient): ' inside the body cannot fool the anchored leading-slot match");

        // And the same body text as a bare non-LlmApiException engine error (no leading slot at all) still gates.
        RealModelGate.IsGatewayInfraError(NodeFailedPayload("InvalidOperationException: a message mentioning , Transient): in passing"))
            .ShouldBeFalse("body prose containing the token but no leading API-error slot is a real fault — it gates");
    }

    /// <summary>The shape the engine actually persists for a node failure: <c>{"error":"…","outputs":{},"duration_ms":…}</c> — so the gate is exercised against the REAL record shape (its `error` field), not a bare string.</summary>
    private static string NodeFailedPayload(string error) =>
        System.Text.Json.JsonSerializer.Serialize(new { error, outputs = new { }, duration_ms = 12 });

    [Theory]
    [InlineData(RealModelOutcome.Drove, "DROVE")]
    [InlineData(RealModelOutcome.CapabilityMiss, "CAPABILITY MISS")]
    [InlineData(RealModelOutcome.CodeFault, "CODE FAULT")]
    public void The_three_way_outcome_is_always_appended_to_the_step_summary(RealModelOutcome outcome, string expectedLabel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-3way-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportThreeWay(outcome, "trajectory=plan→spawn→merge", path);

            var written = File.ReadAllText(path);
            written.ShouldContain(expectedLabel);
            written.ShouldContain("trajectory=plan→spawn→merge");
            // A capability miss states plainly it does not gate — it must never read as a silent green.
            if (outcome == RealModelOutcome.CapabilityMiss) written.ShouldContain("NOT gating");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
