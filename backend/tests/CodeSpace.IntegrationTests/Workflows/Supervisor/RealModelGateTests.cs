using Shouldly;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Pins the real-model gate POLICY (which wires gate CI) + the informational reporting promise. Everything is driven
/// through the gate's PURE seams (the raw-string overload of <see cref="RealModelGate.IsRequired(string,string?)"/> and
/// <see cref="RealModelGate.ReportInformational"/> with an explicit path), so these tests never mutate process-wide env
/// — there is no global state to race a concurrent reader.
/// </summary>
public sealed class RealModelGateTests
{
    [Theory]
    // INFORMATIONAL, never gating: a lane whose runner cannot confine produced its verdict under a materially
    // different sandbox from the privileged gate's, and the reader of an archived summary has no other way to tell.
    [InlineData("/usr/bin/bwrap", null, " [runner=confined]")]
    [InlineData(null, SandboxConfinement.ReasonNotLinux, " [runner=unconfined (not-linux)]")]
    [InlineData(null, SandboxConfinement.ReasonNoBubblewrap, " [runner=unconfined (no-bwrap)]")]
    [InlineData(null, SandboxConfinement.ReasonNoUserNamespaces, " [runner=unconfined (no-userns)]")]
    public void Every_verdict_line_names_the_confinement_its_runner_could_apply(string? available, string? reason, string expected)
    {
        RealModelGate.ConfinementStamp(available, reason).ShouldBe(expected,
            customMessage: "an archived real-model verdict must say which sandbox produced it — 'confined' and 'unconfined' are different experiments");
    }

    [Fact]
    public void The_confinement_stamp_reports_THIS_hosts_real_probe()
    {
        // The live overload must be the probe, not a constant: a stamp that always said "confined" would be worse
        // than none. Anchored on the host's own probe so the assertion is honest on a dev Mac and in the gate alike.
        var expected = BubblewrapSandbox.Available is null ? " [runner=unconfined" : " [runner=confined]";

        RealModelGate.ConfinementStamp().ShouldStartWith(expected);
    }

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
        Should.NotThrow(() => RealModelGate.ReportInformational("OpenAI", ok: false, verdict: "bad", stepSummaryPath: null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_informational_verdict_is_appended_to_the_step_summary_file(bool ok)
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-summary-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportInformational("OpenAI", ok, $"OpenAI trajectory — {(ok ? "drove to completion" : "never stopped")}", path);

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
            // Anthropic is the blessed wire; a gateway timeout must NOT fail the job (no ShouldAssertException), and it
            // must be surfaced loudly — but it must ALSO not read as a PASS. It raises a SkipException, so the runner
            // records the test as NotExecuted: the job stays green, and the trx says the lane measured nothing.
            var skip = await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveAsync("Anthropic",
                () => throw new TaskCanceledException("timeout", new TimeoutException()), gating: true, stepSummaryPath: path));

            skip.Message.ShouldContain("NON-GATING infra skip");

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
    public async Task A_gateway_infra_fault_is_a_SkipException_on_every_gate_entry_point()
    {
        // One shape, four doors: whichever gate a lane calls, an unmeasurable run lands as NotExecuted, never Passed.
        // (This is the defect the arc exists for: a 23-live-call decision gate reported `Passed [1 s]` beside a real
        // `Passed [5 m 28 s]` run of the same test, because the infra skip was written to the step summary alone.)
        Exception Infra() => new TaskCanceledException("timeout", new TimeoutException());

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveAsync("Anthropic",
            () => throw Infra(), gating: true, stepSummaryPath: null));

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveAsync("Anthropic",
            () => throw Infra(), stepSummaryPath: null));   // the three-way (whole-loop outcome) overload

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic",
            () => throw Infra(), attempts: 2, stepSummaryPath: null));

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic",
            () => throw Infra(), attempts: 2, stepSummaryPath: null, attemptDeadline: TimeSpan.FromSeconds(5)));

        // The INFORMATIONAL wire skips too — it never gated, but it never measured anything either.
        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveBestOfNAsync("OpenAI",
            () => throw Infra(), attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public void An_honest_no_credentials_skip_is_a_SkipException_the_caller_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-skipped-{Guid.NewGuid():N}.md");
        try
        {
            // ReportSkipped RETURNS the exception (the call site throws it) so the honest fork/local skip stops being a
            // silent `return;` that the trx recorded as a green pass over zero live calls.
            var skip = RealModelGate.ReportSkipped("Anthropic", "CODESPACE_LLM_* absent (fork/local)", path);

            skip.ShouldBeOfType<SkipException>();
            skip.Message.ShouldContain("NOT EVALUATED");
            File.ReadAllText(path).ShouldContain("NOT EVALUATED");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_informational_wire_fault_writes_a_greppable_console_line_and_never_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-infofail-{Guid.NewGuid():N}.md");
        var console = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);

            // Under CI the step-summary branch is taken, which used to mean the job LOG said nothing at all about an
            // informational wire's fault — a one-second "pass" with no trace. It must now also print to stdout.
            await Should.NotThrowAsync(() => RealModelGate.AssessLiveAsync("OpenAI",
                () => Task.FromResult((false, "OpenAI scored 3/14 golden decisions")), gating: true, stepSummaryPath: path));
        }
        finally
        {
            Console.SetOut(console);
            File.Delete(path);
        }

        var stdout = captured.ToString();
        stdout.ShouldContain("[realmodel] INFORMATIONAL-FAIL");
        stdout.ShouldContain("wire=OpenAI");
        stdout.ShouldContain("scored 3/14");
        stdout.ShouldContain("An_informational_wire_fault_writes_a_greppable_console_line_and_never_throws", Case.Sensitive, "the line names WHICH test measured nothing");
    }

    [Fact]
    public void A_verdict_carries_a_masking_proof_fingerprint()
    {
        // The configured model id is a repository SECRET (masked in the CI log), so the fingerprint is what actually
        // travels: a 25-run red streak can be told apart as "the gateway started answering with another model" only if
        // two runs' stamps can be compared. Same name → same fp; a different name → a different fp.
        RealModelGate.Fingerprint("claude-sonnet-4-5").ShouldBe(RealModelGate.Fingerprint("claude-sonnet-4-5"));
        RealModelGate.Fingerprint("claude-sonnet-4-5").ShouldNotBe(RealModelGate.Fingerprint("claude-opus-4-1"));
        RealModelGate.Fingerprint("claude-sonnet-4-5").Length.ShouldBe(8);

        var path = Path.Combine(Path.GetTempPath(), $"realmodel-stamp-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportThreeWay(RealModelOutcome.Drove, "drove the arc", path);

            File.ReadAllText(path).ShouldContain("fp=");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// GitHub masks a secret in the LOG, never in a FILE — and both the step summary and the trx are uploaded as
    /// artifacts. Run 33723910434's `real-model-results` artifact carried the raw configured model id
    /// (CODESPACE_LLM_MODEL_ID, a repository secret) in its step-summary copy, readable by anyone who could download
    /// it. So the stamp the gate WRITES may carry the fingerprint and the source, never a name — for the observed
    /// name either, which on a pinned lane is the very same id the secret holds.
    /// </summary>
    [Theory]
    [InlineData("gateway-answered-model-xyz", "pinned-secret-model-id", "observed")]   // a live response named a model → its fp, tagged observed
    [InlineData(null, "pinned-secret-model-id", "configured")]                         // nothing answered → fall back to the configured id's fp
    [InlineData("", "pinned-secret-model-id", "configured")]                           // a blank observation is not an observation
    public void The_stamp_the_gate_writes_carries_the_fingerprint_and_its_source_but_never_a_model_name(string? observed, string configured, string expectedSource)
    {
        var stamp = RealModelGate.ModelStamp(observed, configured);
        var fingerprinted = string.IsNullOrWhiteSpace(observed) ? configured : observed;

        stamp.ShouldContain($"({expectedSource}", Case.Sensitive, "an observed name must win over the configured id, and the reader must be told which one answered");
        stamp.ShouldContain($"fp={RealModelGate.Fingerprint(fingerprinted)}", Case.Sensitive, "the fingerprint is taken over the name that actually identified the model this run");
        stamp.ShouldNotContain(configured, Case.Sensitive, "the configured id is a repository SECRET and the artifact is a FILE — masking does not apply");

        if (!string.IsNullOrWhiteSpace(observed))
            stamp.ShouldNotContain(observed, Case.Sensitive, "on a pinned lane the observed name IS the secret; only its fingerprint may be written");
    }

    [Fact]
    public void The_stamp_says_unknown_when_nothing_named_a_model_at_all()
    {
        RealModelGate.ModelStamp(observed: null, configured: null).ShouldBe("[model=unknown fp=none]");
        RealModelGate.ModelStamp(observed: "  ", configured: "  ").ShouldBe("[model=unknown fp=none]");
    }

    [Fact]
    public async Task A_written_verdict_fingerprints_the_model_the_PROVIDER_reported_not_the_one_that_was_asked_for()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-observed-{Guid.NewGuid():N}.md");
        try
        {
            // ObserveModel is what the observing wrapper (ModelObserving.Wrap / .RegisterDecorators) calls with
            // StructuredLLMCompletion.Model. It is
            // written INSIDE the drive closure — deeper in the async flow than the gate that reads it — which is why
            // the sink is a mutable holder rather than a plain AsyncLocal<string>.
            await RealModelGate.AssessLiveAsync("OpenAI", () =>
            {
                RealModelGate.ObserveModel("gateway-answered-model-xyz");
                return Task.FromResult((true, "scored 14/14"));
            }, gating: false, stepSummaryPath: path);

            var written = File.ReadAllText(path);
            written.ShouldContain($"fp={RealModelGate.Fingerprint("gateway-answered-model-xyz")}");
            written.ShouldContain("(observed)", Case.Sensitive, "an observed name must win over the configured id");
            written.ShouldNotContain("gateway-answered-model-xyz", Case.Sensitive, "no model NAME may reach a file the gate writes — the file is uploaded, and upload masks nothing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// "The gateway started answering with another model" is the one thing a name was carried for, and it survives as
    /// a fingerprint COMPARISON — no name, so it is safe in the artifact AND on stdout, which is not a private
    /// channel either (xUnit captures the console into the trx's StdOut, and the trx is uploaded).
    /// </summary>
    [Fact]
    public void The_stamp_reports_a_gateway_answering_with_another_model_as_a_fingerprint_comparison()
    {
        var drifted = RealModelGate.ModelStamp("gateway-answered-model-xyz", "pinned-secret-model-id");

        drifted.ShouldContain($"fp={RealModelGate.Fingerprint("gateway-answered-model-xyz")}", Case.Sensitive, "the model that ANSWERED is the primary fingerprint");
        drifted.ShouldContain($"differs from configured fp={RealModelGate.Fingerprint("pinned-secret-model-id")}", Case.Sensitive, "the drift is actionable only if the reader can see WHICH pin it drifted from");
        drifted.ShouldNotContain("gateway-answered-model-xyz", Case.Sensitive, "not even the observed name — the console lands in the trx too");
        drifted.ShouldNotContain("pinned-secret-model-id", Case.Sensitive);

        RealModelGate.ModelStamp("pinned-secret-model-id", "pinned-secret-model-id").ShouldNotContain("differs", Case.Sensitive, "the gateway answered with exactly what was asked for — there is no drift to report");
        RealModelGate.ModelStamp(null, "pinned-secret-model-id").ShouldNotContain("differs", Case.Sensitive, "nothing was observed, so nothing can be said to differ");
    }

    /// <summary>
    /// The gate writes no model name, but other components still log one — <c>LlmCompleteNode</c> logs
    /// "LLM completion {Model} …" with the very value handed to <see cref="RealModelGate.ObserveModel"/>, and xUnit
    /// captures that into the trx's StdOut (which is how run 33754366815's footer-signals trx shipped a provider
    /// model name). So the gate hands the collect step the exact VALUES to strike, in a file beside the step
    /// summaries — redacting by log-message SHAPE would break the moment a template changed.
    /// </summary>
    [Fact]
    public void The_observed_model_names_are_recorded_for_the_collect_step_to_redact()
    {
        RealModelGate.ObservedModelsFileName.ShouldBe("codespace_observed_models", "the collect script reads this exact filename — renaming it here alone stops redacting observed model names");

        var script = Path.Combine(RepositoryRoot(), ".github", "scripts", "collect-real-model-verdicts.sh");
        File.Exists(script).ShouldBeTrue($"the collect script must exist at {script}");
        File.ReadAllText(script).ShouldContain(RealModelGate.ObservedModelsFileName, Case.Sensitive, "the two halves of the side-file contract must name the same file");
    }

    /// <summary>Walk up from the test binary to the repository root (the directory holding .github).</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("no .github directory above the test binary");
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
                () => Task.FromResult((false, "whole-loop: no conformant decision")), gating: false, stepSummaryPath: path));

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
            // Non-gating (no ShouldAssertException) but NOT a pass either: it raises a SkipException so the trx records
            // the lane as NotExecuted rather than green.
            await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveAsync("Anthropic",
                () => throw new TaskCanceledException("timeout", new TimeoutException()), stepSummaryPath: path));

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

    [Fact]
    public void The_model_plane_parks_own_honest_ending_is_infra_and_no_model_authored_stop_can_impersonate_it()
    {
        // An outage that outlives the whole 24h park window ends the run through the ledger as a clean `stop` on a
        // Success walk. Every whole-loop evaluator scores a clean stop a CapabilityMiss — a red for a run whose model
        // was never able to answer at all — so the reason has to be readable, and this is what reads it.
        RealModelGate.IsModelPlaneUnavailableStop(ForcedStopPayload(SupervisorStopReasons.ModelPlaneUnavailable))
            .ShouldBeTrue("the park's honest ending is the outage wearing the product's graceful exit — it routes to the non-gating skip, never a capability red");

        // Every OTHER forced stop is a real verdict about the run and must keep gating exactly as before.
        RealModelGate.IsModelPlaneUnavailableStop(ForcedStopPayload(SupervisorStopReasons.NoProgress)).ShouldBeFalse("a stalled run IS a capability outcome");
        RealModelGate.IsModelPlaneUnavailableStop(ForcedStopPayload(SupervisorStopReasons.CostCapReached)).ShouldBeFalse("a spent budget is the operator's bound, not an outage");

        // A MODEL-authored stop cannot impersonate it: the projector emits outcome + summary, never a `reason` field —
        // which is why reading `reason` can never launder a genuine miss into a skip, even if a model echoes the words.
        RealModelGate.IsModelPlaneUnavailableStop(System.Text.Json.JsonSerializer.Serialize(new { outcome = "failed", summary = $"I gave up: {SupervisorStopReasons.ModelPlaneUnavailable}" }))
            .ShouldBeFalse("a model writing the phrase into its own summary is still a model-authored stop — it gates");

        RealModelGate.IsModelPlaneUnavailableStop(null).ShouldBeFalse();
        RealModelGate.IsModelPlaneUnavailableStop("").ShouldBeFalse();
        RealModelGate.IsModelPlaneUnavailableStop("not json at all").ShouldBeFalse();
    }

    [Fact]
    public void Only_a_tape_holding_NOTHING_but_the_forced_stop_routes_to_the_infra_skip()
    {
        // The "nothing was measured" half the stop check deliberately refuses to guess at. A tape of exactly ONE
        // decision — the engine's forced stop — is a run whose model never got a turn, so there is no capability
        // verdict to score and the outage routes to the non-gating skip.
        var forcedStop = ForcedStopPayload(SupervisorStopReasons.ModelPlaneUnavailable);

        RealModelGate.IsWholeWindowModelPlaneOutage(new[] { forcedStop })
            .ShouldBeTrue("one decision, and it is the park's own ending — the attempt took no model turn at all");

        // An attempt whose model DID decide before the plane went down has something measured, and keeps today's
        // scoring. Skipping it would refund a real capability outcome on the strength of how the run happened to end.
        RealModelGate.IsWholeWindowModelPlaneOutage(new[] { ModelPlanPayload(), forcedStop })
            .ShouldBeFalse("a model turn preceded the outage — that turn IS the measurement, so this attempt still gates");

        RealModelGate.IsWholeWindowModelPlaneOutage(Array.Empty<string>()).ShouldBeFalse("no decisions at all is not this shape — it is a run that never reached the brain");
        RealModelGate.IsWholeWindowModelPlaneOutage(new[] { ModelPlanPayload() }).ShouldBeFalse("one MODEL-authored decision is a measured turn, not an outage");
    }

    /// <summary>A model-authored decision as the projector persists it — <c>outcome</c>/<c>summary</c>, never a <c>reason</c>.</summary>
    private static string ModelPlanPayload() =>
        System.Text.Json.JsonSerializer.Serialize(new { goal = "ship it", subtasks = new[] { new { id = "s1", title = "Audit" } } });

    /// <summary>The shape the engine actually persists for a node failure: <c>{"error":"…","outputs":{},"duration_ms":…}</c> — so the gate is exercised against the REAL record shape (its `error` field), not a bare string.</summary>
    private static string NodeFailedPayload(string error) =>
        System.Text.Json.JsonSerializer.Serialize(new { error, outputs = new { }, duration_ms = 12 });

    /// <summary>The shape <c>SupervisorTurnService.ForcedStop</c> persists for an engine-forced terminal: <c>{"reason":"…","detail":null}</c>.</summary>
    private static string ForcedStopPayload(string reason) =>
        System.Text.Json.JsonSerializer.Serialize(new { reason, detail = (string?)null });

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

    // ── Typed LlmApiException on the EXCEPTION path (trajectory / arbiter catch the throw directly) ──

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]
    [InlineData(LlmErrorCategory.RateLimited, true)]
    [InlineData(LlmErrorCategory.AuthFailed, true)]
    [InlineData(LlmErrorCategory.Malformed, false)]
    [InlineData(LlmErrorCategory.BadRequest, false)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded, false)]
    [InlineData(LlmErrorCategory.ContentFiltered, false)]
    public void A_typed_LlmApiException_is_infra_for_the_propagated_gateway_categories_only(LlmErrorCategory category, bool isInfra)
    {
        // The decider PROPAGATES the infra categories (Transient/RateLimited/AuthFailed) as a typed throw rather than
        // fail-closing them, so the EXCEPTION path (trajectory/arbiter best-of-N) must treat those exactly as the
        // string-based IsGatewayInfraError treats the persisted node-failed record: non-gating infra. The model-CAPABILITY
        // categories (Malformed/BadRequest/ContextLengthExceeded/ContentFiltered) are a real miss and must GATE, not skip.
        var ex = new LlmApiException("Anthropic", null, category, "boom");

        RealModelGate.IsGatewayInfraFailure(ex).ShouldBe(isInfra);
    }

    [Fact]
    public void A_typed_transient_LlmApiException_nested_in_an_aggregate_is_still_infra()
    {
        // The await chain can surface it nested; Unwrap flattens — so the real trajectory throw ("the request timed out
        // before the gateway responded") is recognised as non-gating infra even when wrapped, not mis-gated as a miss.
        var inner = new LlmApiException("Anthropic", null, LlmErrorCategory.Transient, "the request timed out before the gateway responded");

        RealModelGate.IsGatewayInfraFailure(new AggregateException(inner)).ShouldBeTrue();
    }

    // ── Agent-execution infra fault: an all-failed fan-out of the deterministic exit-0 fake is INFRA, not a model miss ──

    [Fact]
    public void An_agent_execution_infra_fault_is_recognised_as_non_gating_infra_but_a_real_drive_bug_still_gates()
    {
        // The deterministic exit-0 fake agent cannot CHOOSE to fail, so an all-failed fan-out is an OS/sandbox/capture
        // fault — routed to the SAME non-gating skip as a gateway timeout (and flattened through an aggregate, as the
        // await chain may wrap it). A genuine logic bug is NEVER misread as this infra.
        RealModelGate.IsGatewayInfraFailure(new AgentExecutionInfraException("agents could not execute")).ShouldBeTrue();
        RealModelGate.IsGatewayInfraFailure(new AggregateException(new AgentExecutionInfraException("agents broke"))).ShouldBeTrue("the await chain can wrap it");
        RealModelGate.IsGatewayInfraFailure(new InvalidOperationException("a real engine bug")).ShouldBeFalse("a logic bug must gate, never read as execution infra");
    }

    /// <summary>
    /// The precondition BOTH other classifiers assume and neither checks: that the agents ran the FAKE. ANY off-stub
    /// run loses control — an all-or-nothing predicate would be disarmed by a single surviving stubbed run, and the
    /// harness is rewritten PER DISPATCH so mixed fan-outs are the normal case, not an edge one. The third row is the
    /// exact shape that used to launder: one codex run present, so a naive check passes, while zero agents succeeded
    /// so ClassifyAgentExecution then refunds the whole thing as infra.
    /// </summary>
    /// <summary>
    /// A gating:false arm must never RED the job — and it did. Run 30809950520's stop-DoD arm died on a
    /// JsonException because the live model authored a stop payload missing a `required` property; the arm never
    /// reached a verdict, and a lane declared report-only failed the whole job on it.
    /// </summary>
    [Fact]
    public async Task A_report_only_arm_that_FAULTS_reports_instead_of_reddening_the_job()
    {
        var summary = Path.Combine(Path.GetTempPath(), $"gate-armfault-{Guid.NewGuid():N}.md");

        try
        {
            await RealModelGate.AssessLiveAsync("Anthropic", () => throw new System.Text.Json.JsonException("missing required properties including: 'outcome'"), gating: false, stepSummaryPath: summary);

            var written = await File.ReadAllTextAsync(summary);
            written.ShouldContain("FAULTED before reaching a verdict", Case.Insensitive, "the fault is surfaced, not silently swallowed");
            written.ShouldContain("JsonException", Case.Insensitive, "the reader needs the actual fault to act on it");
        }
        finally { File.Delete(summary); }
    }

    /// <summary>
    /// The carve-out that keeps this from disarming the thing it protects: some report-only arms assert HARD inside
    /// the closure ON PURPOSE — the S1 handoff arm documents exactly that ("the handoff MECHANISM is asserted HARD
    /// (Shouldly, bypassing the soft report-only gate)"). An assertion is the arm SPEAKING; anything else is the arm
    /// BREAKING. Only the second is absorbed.
    /// </summary>
    [Fact]
    public async Task A_report_only_arm_that_ASSERTS_still_fails_because_that_assertion_is_deliberate()
    {
        var summary = Path.Combine(Path.GetTempPath(), $"gate-armassert-{Guid.NewGuid():N}.md");

        // Caught by hand, not with Should.ThrowAsync: Shouldly deliberately refuses to capture a
        // ShouldAssertException thrown by the delegate, because doing so would mask real assertion failures inside it.
        var propagated = false;

        try
        {
            await RealModelGate.AssessLiveAsync("Anthropic", () => throw new ShouldAssertException("handoffWorked should be True but was False"), gating: false, stepSummaryPath: summary);
        }
        catch (ShouldAssertException)
        {
            propagated = true;
        }
        finally { File.Delete(summary); }

        propagated.ShouldBeTrue("a report-only arm's OWN hard assertion must still fail the job — that is the documented bypass the S1 handoff arm relies on");
    }

    /// <summary>A GATING arm keeps propagating everything — a required wire that breaks is a real problem, not something to report past.</summary>
    [Fact]
    public async Task A_gating_arm_still_propagates_a_fault()
    {
        var summary = Path.Combine(Path.GetTempPath(), $"gate-armgating-{Guid.NewGuid():N}.md");

        try
        {
            await Should.ThrowAsync<System.Text.Json.JsonException>(() =>
                RealModelGate.AssessLiveAsync("Anthropic", () => throw new System.Text.Json.JsonException("boom"), gating: true, stepSummaryPath: summary));
        }
        finally { File.Delete(summary); }
    }

    [Theory]
    [InlineData("codex-cli", "codex-cli", false, "codex-cli=1")]
    [InlineData("codex-cli,codex-cli", "codex-cli", false, "codex-cli=2")]
    [InlineData("codex-cli,claude-code,claude-code", "codex-cli", true, "claude-code=2, codex-cli=1")]
    [InlineData("claude-code,claude-code", "codex-cli", true, "claude-code=2")]
    [InlineData("claude-code", "codex-cli,claude-code", false, "claude-code=1")]
    [InlineData("claude-code,claude-code", "", false, "claude-code=2")]
    [InlineData("", "codex-cli", false, "agents=0")]
    public void ClassifyHarnessControl_flags_any_run_on_a_harness_the_fake_never_armed(string harnessCsv, string stubbedCsv, bool expectLost, string expectCensus)
    {
        var harnesses = harnessCsv.Length == 0 ? Array.Empty<string>() : harnessCsv.Split(',');
        var stubbed = stubbedCsv.Length == 0 ? Array.Empty<string>() : stubbedCsv.Split(',');

        var (lostControl, census) = RealModelGate.ClassifyHarnessControl(harnesses, stubbed);

        lostControl.ShouldBe(expectLost);
        census.ShouldBe(expectCensus, "the census is the whole diagnostic payload — it must name every harness and how many ran on it");
    }

    [Fact]
    public void ClassifyHarnessControl_is_not_disarmed_by_one_surviving_stubbed_run()
    {
        // The concrete false green this exists to kill: a mixed fan-out where nothing succeeded. Without the control
        // check, ClassifyAgentExecution sees succeeded==0 and refunds it as an execution-infra fault, so the arm costs
        // nothing and reads as a flaky runner — when in fact two agents ran a REAL CLI the fake never touched.
        RealModelGate.ClassifyHarnessControl(new[] { "codex-cli", "claude-code", "claude-code" }, new[] { "codex-cli" })
            .LostControl.ShouldBeTrue("one stubbed run among unstubbed ones is still lost control — the premise is about EVERY agent, not about at least one");

        RealModelGate.ClassifyAgentExecution(new[] { AgentRunStatus.Failed, AgentRunStatus.Failed, AgentRunStatus.Failed })
            .ExecutionInfraFault.ShouldBeTrue("...and this is what it would have been laundered into, which is why the control check must run FIRST");
    }

    [Theory]
    // statuses encoded as a CSV of Succeeded(s)/Failed(f)/Queued(q) — the verdict's execution-health signal
    [InlineData("", false, "agents=0")]                       // never fanned out → a plan-only park is a GENUINE miss (gates), NOT infra
    [InlineData("f", true, "agents=1 (0 succeeded, 1 failed)")]   // single agent failed to execute → infra
    [InlineData("f,f,f", true, "agents=3 (0 succeeded, 3 failed)")]   // every agent failed → infra (the observed runner break)
    [InlineData("s", false, "agents=1 (1 succeeded, 0 failed)")]      // an agent succeeded → execution works; any shortfall is the MODEL's → gates
    [InlineData("s,f", false, "agents=2 (1 succeeded, 1 failed)")]    // partial success → NOT infra (the path works), a shortfall gates
    [InlineData("f,q", true, "agents=2 (0 succeeded, 1 failed)")]     // none succeeded (a stuck/queued + a failed) → still an execution-infra fault
    public void ClassifyAgentExecution_separates_an_execution_infra_fault_from_a_model_miss(string csv, bool expectInfra, string expectSummary)
    {
        var statuses = csv.Length == 0
            ? new List<AgentRunStatus>()
            : csv.Split(',').Select(c => c switch { "s" => AgentRunStatus.Succeeded, "f" => AgentRunStatus.Failed, _ => AgentRunStatus.Queued }).ToList();

        var (infra, summary) = RealModelGate.ClassifyAgentExecution(statuses);

        infra.ShouldBe(expectInfra);
        summary.ShouldContain(expectSummary);
    }

    [Theory]
    // deterministicFakeAgents, spawnedAndMerged, succeededAgents, realPatchCount → isCaptureInfra
    [InlineData(true, true, 6, 0, true)]    // headline fake: spawned+merged, 6 succeeded, 0 patches → the file write / capture broke → infra
    [InlineData(true, true, 1, 0, true)]    // a single succeeded fake with no captured patch is still a capture fault
    [InlineData(true, true, 3, 1, false)]   // a patch WAS captured → the path works; any shortfall is the model's → gates
    [InlineData(true, false, 6, 0, false)]  // never merged (parked before integrating) → a genuine model miss, NOT capture infra
    [InlineData(true, true, 0, 0, false)]   // zero succeeded → the all-FAILED case (ClassifyAgentExecution owns it), not this one
    [InlineData(false, true, 6, 0, false)]  // REAL coding agent: 0 patches is a legit "didn't edit" capability outcome → must gate, never skip
    public void IsCaptureInfraFault_skips_only_a_deterministic_fake_that_merged_with_succeeded_agents_but_no_patch(
        bool deterministicFakes, bool spawnedAndMerged, int succeeded, int patches, bool expected)
    {
        RealModelGate.IsCaptureInfraFault(deterministicFakes, spawnedAndMerged, succeeded, patches).ShouldBe(expected);
    }

    [Fact]
    public async Task AssessLiveAsync_treats_an_agent_execution_fault_as_non_gating_even_for_the_blessed_wire()
    {
        // The report-only reaction-arc path (three-way AssessLiveAsync) must NOT red the blessed wire when the agents
        // could not execute — it is infra, surfaced loudly, never a code-fault gate.
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-execinfra-{Guid.NewGuid():N}.md");
        try
        {
            Func<Task<(RealModelOutcome Outcome, string Note)>> drive =
                () => throw new AgentExecutionInfraException("the spawned agents could not execute — agents=2 (0 succeeded, 2 failed)");

            await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveAsync("Anthropic", drive, stepSummaryPath: path));

            var written = File.ReadAllText(path);
            written.ShouldContain("NON-GATING infra skip");
            written.ShouldContain("Anthropic");
            written.ShouldContain("could not execute");   // the reason names the agent break, honestly (not "gateway timed out")
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Strict whole-loop gate: real-model-DROVE-to-completion is the criterion (CapabilityMiss REDS, best-of-N flake-safe) ──

    [Fact]
    public void Strict_whole_loop_attempt_budget_constants_are_pinned()
    {
        // Renaming the env var breaks an operator who raised N via env; the default is the flake-vs-cost knob.
        RealModelGate.WholeLoopAttemptsEnvVar.ShouldBe("CODESPACE_REALMODEL_WHOLE_LOOP_ATTEMPTS");
        RealModelGate.DefaultWholeLoopAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task The_strict_gate_passes_on_any_Drove_among_the_N_attempts()
    {
        // best-of-N: a first-attempt capability miss followed by a Drove PASSES — one off-run never reds main.
        var (drive, calls) = Sequence(RealModelOutcome.CapabilityMiss, RealModelOutcome.Drove);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        calls().ShouldBe(2, "it retried after the miss and stopped on the Drove");
    }

    [Fact]
    public async Task The_strict_gate_reds_the_blessed_wire_when_EVERY_attempt_is_a_capability_miss()
    {
        // The real-model-DROVE-to-completion criterion: a model that RAN but parked short in ALL N attempts REDS — it is
        // not a "reported" footnote. (Plain try/catch — async Should.ThrowAsync does not reliably catch Shouldly's type.)
        var (drive, calls) = Sequence(RealModelOutcome.CapabilityMiss, RealModelOutcome.CapabilityMiss);

        Shouldly.ShouldAssertException? caught = null;
        try { await RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null); }
        catch (Shouldly.ShouldAssertException ex) { caught = ex; }

        caught.ShouldNotBeNull("N capability misses on the blessed wire must red — the model did not drive to completion");
        calls().ShouldBe(2, "it used the full best-of-N budget before gating");
        // The gate names WHY each attempt parked short (the per-attempt verdict) so a CI red is diagnosable from the console log.
        caught!.Message.ShouldContain("verdict#1");
        caught.Message.ShouldContain("verdict#2");
    }

    [Fact]
    public async Task The_strict_gate_reds_immediately_on_a_code_fault_and_never_retries_it()
    {
        // A CodeFault is a real regression, not capability variance — it reds on attempt 1 and is NEVER retried.
        var (drive, calls) = Sequence(RealModelOutcome.CodeFault, RealModelOutcome.Drove);

        var gated = false;
        try { await RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null); }
        catch (Shouldly.ShouldAssertException) { gated = true; }

        gated.ShouldBeTrue("a CodeFault reds the blessed wire");
        calls().ShouldBe(1, "a CodeFault is never retried — the Drove that would have followed is never reached");
    }

    [Fact]
    public async Task A_gateway_infra_attempt_is_non_gating_and_does_NOT_consume_a_capability_slot()
    {
        // An infra (timeout) attempt is a non-gating LOUD skip that does not burn a capability slot — so infra→miss→Drove
        // still PASSES on a 2-attempt budget (the Drove is reached only because the infra attempt did not count).
        var (drive, calls) = Sequence(new TimeoutException("gateway slow"), RealModelOutcome.CapabilityMiss, RealModelOutcome.Drove);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        calls().ShouldBe(3, "the infra attempt did not consume a capability slot, so the later Drove was reached");
    }

    [Fact]
    public async Task An_all_infra_run_is_a_non_gating_skip_never_a_gate()
    {
        // Every attempt times out → misses never reaches N → non-gating infra skip, never a gate. A slow gateway can't
        // red — and can no longer report a green PASS either: the run is recorded as NotExecuted.
        var (drive, _) = Sequence(new TimeoutException("a"), new TimeoutException("b"), new TimeoutException("c"), new TimeoutException("d"), new TimeoutException("e"));

        var skip = await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        skip.ShouldNotBeOfType<ShouldAssertException>("an infra skip must never gate the blessed wire");
    }

    [Fact]
    public async Task An_agent_execution_infra_attempt_does_NOT_consume_a_capability_slot()
    {
        // The runner-side break we observed: the spawned agents could not execute. That attempt is non-gating infra
        // (NOT a CapabilityMiss), so execution-infra→miss→Drove still PASSES on a 2-attempt budget — the broken-agent
        // attempt did not burn a capability slot, exactly like a gateway timeout.
        var (drive, calls) = Sequence(new AgentExecutionInfraException("agents=2 (0 succeeded, 2 failed)"), RealModelOutcome.CapabilityMiss, RealModelOutcome.Drove);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        calls().ShouldBe(3, "the execution-infra attempt did not consume a capability slot, so the later Drove was reached");
    }

    [Fact]
    public async Task A_whole_loop_attempt_that_exceeds_the_deadline_is_a_did_not_converge_miss_not_a_job_hang()
    {
        // The hang we observed in CI: an attempt blocked in a stuck agent / gateway call that never returned, riding to
        // the job's 60-min wall-clock cap (one stuck test silently killing the whole job). The per-attempt deadline must
        // ABORT it as a non-converging MISS — so a persistent hang REDs the blessed wire FAST + bounded, never the cap.
        Func<Task<(RealModelOutcome Outcome, string Note)>> hangs = async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));   // a stuck drive that never returns on its own
            return (RealModelOutcome.Drove, "would have driven had it not been aborted");
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Shouldly.ShouldAssertException? caught = null;
        try { await RealModelGate.AssessLiveWholeLoopAsync("Anthropic", hangs, attempts: 2, stepSummaryPath: null, attemptDeadline: TimeSpan.FromMilliseconds(50)); }
        catch (Shouldly.ShouldAssertException ex) { caught = ex; }
        sw.Stop();

        caught.ShouldNotBeNull("two deadline-exceeded attempts are non-converging misses → the blessed wire REDs (not a silent infra skip, not a hang)");
        caught!.Message.ShouldContain("did not converge", Case.Insensitive);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the deadline aborted each hung attempt — the gate did NOT wait 30s for driveOnce, proving the job-cap hang is bounded away");
    }

    [Fact]
    public void The_whole_loop_attempt_deadline_env_var_and_default_are_pinned()
    {
        // Rule 8: renaming this env var or changing the default silently changes the CI hang-bounding behaviour — pin both.
        RealModelGate.WholeLoopAttemptDeadlineEnvVar.ShouldBe("CODESPACE_REALMODEL_WHOLE_LOOP_ATTEMPT_DEADLINE_SECONDS");
        RealModelGate.DefaultWholeLoopAttemptDeadlineSeconds.ShouldBe(600);
    }

    [Fact]
    public async Task An_all_execution_infra_run_is_a_non_gating_skip_never_a_gate()
    {
        // Every attempt is a runner-side agent-execution break → misses never reach N → non-gating infra skip, never a
        // gate. This is the fix for the observed false-red: a runner that cannot execute the fake agent can no longer
        // red main as a phantom CapabilityMiss.
        var (drive, _) = Sequence(new AgentExecutionInfraException("a"), new AgentExecutionInfraException("b"), new AgentExecutionInfraException("c"), new AgentExecutionInfraException("d"), new AgentExecutionInfraException("e"));

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public async Task The_strict_gate_never_gates_an_informational_wire()
    {
        // An informational wire never reds even on N capability misses — only the blessed wire gates.
        var (drive, _) = Sequence(RealModelOutcome.CapabilityMiss, RealModelOutcome.CapabilityMiss);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveWholeLoopAsync("OpenAI", drive, attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public async Task The_strict_gate_never_swallows_a_non_infra_exception()
    {
        // A real bug while driving (not a gateway failure) PROPAGATES, never masked as a skip — same teeth as the overloads.
        Func<Task<(RealModelOutcome Outcome, string Note)>> drive = () => throw new InvalidOperationException("harness bug");

        await Should.ThrowAsync<InvalidOperationException>(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public void A_no_secret_skip_is_reported_as_NOT_EVALUATED_and_never_reads_as_a_pass()
    {
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-skip-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportSkipped("Anthropic", "CODESPACE_LLM_* absent (fork/local)", path);

            var written = File.ReadAllText(path);
            written.ShouldContain("NOT EVALUATED");
            written.ShouldContain("skip");
            written.ShouldContain("Anthropic");
            written.Contains("✅").ShouldBeFalse("a skip must never be styled as a pass — the Drove pass icon must be absent");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A driveOnce factory yielding the given steps in order (a <see cref="RealModelOutcome"/> → returned; an <see cref="Exception"/> → thrown), repeating the last step if called beyond the list. The companion func returns the invocation count — so a test pins that a CodeFault is never retried / best-of-N spent its budget.</summary>
    private static (Func<Task<(RealModelOutcome Outcome, string Note)>> Drive, Func<int> Calls) Sequence(params object[] steps)
    {
        var calls = 0;

        Func<Task<(RealModelOutcome Outcome, string Note)>> drive = async () =>
        {
            var step = steps[Math.Min(calls, steps.Length - 1)];
            calls++;
            await Task.Yield();
            return step is Exception ex ? throw ex : ((RealModelOutcome)step, $"verdict#{calls}");
        };

        return (drive, () => calls);
    }

    // ── Boolean best-of-N eval gate (trajectory / arbiter): any-Ok passes, all-fail reds, informational runs once ──

    [Fact]
    public void Eval_best_of_N_constants_are_pinned()
    {
        RealModelGate.EvalAttemptsEnvVar.ShouldBe("CODESPACE_REALMODEL_EVAL_ATTEMPTS");
        RealModelGate.DefaultEvalAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task The_eval_best_of_N_passes_on_any_Ok_among_the_N_attempts()
    {
        // A first-attempt fail followed by an Ok PASSES — a single non-deterministic off-run never reds main.
        var (drive, calls) = BoolSequence(false, true);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        calls().ShouldBe(2, "it retried after the fail and stopped on the Ok");
    }

    [Fact]
    public async Task The_eval_best_of_N_reds_the_blessed_wire_when_EVERY_attempt_fails_and_names_each_verdict()
    {
        var (drive, calls) = BoolSequence(false, false);

        Shouldly.ShouldAssertException? caught = null;
        try { await RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null); }
        catch (Shouldly.ShouldAssertException ex) { caught = ex; }

        caught.ShouldNotBeNull("N failing attempts on the blessed wire must red");
        calls().ShouldBe(2, "it used the full best-of-N budget before gating");
        caught!.Message.ShouldContain("verdict#1");
        caught.Message.ShouldContain("verdict#2");
    }

    [Fact]
    public async Task An_informational_wire_runs_ONCE_and_never_gates_even_on_a_fail()
    {
        // The non-blessed wire never gates → a single reported attempt (best-of-N is a gating-only concern; saves N× cost).
        var (drive, calls) = BoolSequence(false, false);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveBestOfNAsync("OpenAI", drive, attempts: 3, stepSummaryPath: null));

        calls().ShouldBe(1, "an informational wire does NOT spend the best-of-N budget");
    }

    [Fact]
    public async Task An_eval_infra_attempt_is_non_gating_and_does_NOT_consume_a_slot()
    {
        // infra→fail→Ok still PASSES on a 2-attempt budget — the infra attempt did not burn a capability slot.
        var (drive, calls) = BoolSequence(new TimeoutException("gateway slow"), false, true);

        await Should.NotThrowAsync(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        calls().ShouldBe(3, "the infra attempt did not consume a slot, so the later Ok was reached");
    }

    [Fact]
    public async Task The_eval_best_of_N_never_swallows_a_non_infra_exception()
    {
        Func<Task<(bool Ok, string Verdict)>> drive = () => throw new InvalidOperationException("harness bug");

        await Should.ThrowAsync<InvalidOperationException>(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public async Task An_infra_exhausted_budget_that_still_MEASURED_a_fail_verdict_is_not_reported_as_a_skip()
    {
        // infra, infra, infra, fail on a budget of 2: the attempt budget (2 + 2 infra retries = 4) runs out with ONE
        // real fail verdict recorded. That verdict is a measurement — the model answered and answered wrong — so it
        // must NOT be re-labelled NotExecuted. It also does not GATE (one fail < the 2-attempt floor), so the honest
        // result is a plain green pass carrying the reported verdict.
        var (drive, calls) = BoolSequence(new TimeoutException("a"), new TimeoutException("b"), new TimeoutException("c"), false);

        var thrown = await Record.ExceptionAsync(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        thrown.ShouldBeNull("a measured fail verdict below the gating floor is neither a gate nor a skip");
        calls().ShouldBe(4, "the three infra attempts consumed no capability slot, so the fail verdict was reached");
    }

    [Fact]
    public async Task An_all_infra_boolean_eval_that_measured_NOTHING_is_still_a_skip()
    {
        // The other side of the same boundary: every attempt was infra, so zero verdicts exist. That IS unmeasured and
        // must stay a SkipException — the fix for the case above must not disarm the skip entirely.
        var (drive, _) = BoolSequence(new TimeoutException("a"), new TimeoutException("b"), new TimeoutException("c"), new TimeoutException("d"), new TimeoutException("e"));

        await Should.ThrowAsync<SkipException>(() => RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));
    }

    [Fact]
    public async Task A_whole_loop_run_that_MEASURED_a_miss_before_infra_ate_the_budget_is_not_reported_as_a_skip()
    {
        // Same boundary on the strict whole-loop gate: one real CapabilityMiss was measured before infra exhausted the
        // rest of the budget. Non-gating (one miss < the 2-attempt floor) but NOT NotExecuted.
        var (drive, _) = Sequence(new TimeoutException("a"), new TimeoutException("b"), new TimeoutException("c"), RealModelOutcome.CapabilityMiss);

        var thrown = await Record.ExceptionAsync(() => RealModelGate.AssessLiveWholeLoopAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null));

        thrown.ShouldBeNull("a measured CapabilityMiss below the gating floor is neither a gate nor a skip");
    }

    [Fact]
    public async Task A_blessed_best_of_N_attempt_is_tagged_ATTEMPT_FAIL_not_INFORMATIONAL_FAIL()
    {
        // Live proof this was wrong: job 100548613534 printed `INFORMATIONAL-FAIL … wire=Anthropic` for a blessed
        // best-of-N attempt. The two tags mean opposite things to a reader — "ignore, never gates" versus "attempt 1
        // of N on the wire that DOES gate" — so a blessed attempt must never borrow the informational one.
        var console = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);

            var (drive, _) = BoolSequence(false, true);   // attempt 1 fails, attempt 2 passes → the eval passes
            await RealModelGate.AssessLiveBestOfNAsync("Anthropic", drive, attempts: 2, stepSummaryPath: null);
        }
        finally
        {
            Console.SetOut(console);
        }

        var stdout = captured.ToString();
        stdout.ShouldContain("[realmodel] ATTEMPT-FAIL");
        stdout.ShouldContain("wire=Anthropic");
        stdout.ShouldNotContain("INFORMATIONAL-FAIL", Case.Sensitive, "the blessed wire's attempt is not an informational verdict");
        stdout.ShouldContain("A_blessed_best_of_N_attempt_is_tagged_ATTEMPT_FAIL_not_INFORMATIONAL_FAIL", Case.Sensitive, "the attempt line names WHICH test it came from");
    }

    [Fact]
    public void A_whole_loop_summary_line_names_the_test_arm_that_produced_it()
    {
        // A whole-loop job runs a dozen arms into ONE step summary, so an unattributed "CAPABILITY MISS" line cannot be
        // traced back to the arm that produced it.
        var path = Path.Combine(Path.GetTempPath(), $"realmodel-3way-attrib-{Guid.NewGuid():N}.md");
        try
        {
            RealModelGate.ReportThreeWay(RealModelOutcome.CapabilityMiss, "parked at plan", path);

            File.ReadAllText(path).ShouldContain("A_whole_loop_summary_line_names_the_test_arm_that_produced_it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Boolean analogue of <see cref="Sequence"/>: yields (Ok, "verdict#N") for a <see cref="bool"/> step, throws an <see cref="Exception"/> step.</summary>
    private static (Func<Task<(bool Ok, string Verdict)>> Drive, Func<int> Calls) BoolSequence(params object[] steps)
    {
        var calls = 0;

        Func<Task<(bool Ok, string Verdict)>> drive = async () =>
        {
            var step = steps[Math.Min(calls, steps.Length - 1)];
            calls++;
            await Task.Yield();
            return step is Exception ex ? throw ex : ((bool)step, $"verdict#{calls}");
        };

        return (drive, () => calls);
    }
}
