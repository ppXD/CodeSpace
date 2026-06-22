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
}
