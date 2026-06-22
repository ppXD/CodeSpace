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
}
