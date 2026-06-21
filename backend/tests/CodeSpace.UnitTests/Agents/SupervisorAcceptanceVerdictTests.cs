using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins A3 — the OBJECTIVE resolve verdict. <see cref="SupervisorOutcome.ReadResolutionVerdict"/> now prefers a
/// folded server grade (AND-ed with the resolver's self-report marker, so the grade can only TIGHTEN) and falls back
/// BYTE-IDENTICALLY to the marker-only read when no grade was folded. Plus the pure fold helpers
/// (<see cref="SupervisorOutcome.FoldAcceptanceGrade"/> re-emits the agent fields byte-intact + appends the grade;
/// <see cref="SupervisorOutcome.ReadAcceptanceGradePassed"/> reads it back / null when absent) and the timeout const pin.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAcceptanceVerdictTests
{
    private static readonly string Marker = SupervisorResolverRecipe.TestsPassedMarker;

    // ── The verdict: folded grade ANDs with the marker; absent grade = byte-identical marker read ──

    [Theory]
    [InlineData(true, true, "Verified")]    // grade passed + marker present → accepted
    [InlineData(false, true, "Unverified")] // grade FAILED + marker present → the objective grade TIGHTENS over the self-report (the regression A3 fixes)
    [InlineData(true, false, "Unverified")] // grade passed but the resolver itself didn't claim success → AND withholds
    [InlineData(false, false, "Unverified")]
    public void A_folded_grade_is_anded_with_the_marker(bool gradePassed, bool markerPresent, string expected)
    {
        var outcome = ResolveOutcome("Succeeded", markerPresent ? $"done {Marker}" : "done", gradePassed);

        SupervisorOutcome.ReadResolutionVerdict(outcome).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, "Verified")]    // no grade folded → the EXACT pre-A3 marker read (byte-identical fallback)
    [InlineData(false, "Unverified")]
    public void With_no_folded_grade_the_marker_read_is_unchanged(bool markerPresent, string expected)
    {
        var outcome = ResolveOutcome("Succeeded", markerPresent ? $"done {Marker}" : "done", gradePassed: null);

        SupervisorOutcome.ReadResolutionVerdict(outcome).ToString().ShouldBe(expected);
    }

    [Fact]
    public void A_resolve_with_no_folded_result_is_unknown_regardless_of_grade()
    {
        // Still-parked (no agent result folded yet) stays Unknown — the grade can't precede the resolver's result.
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1 }, AgentJson.Options);

        SupervisorOutcome.ReadResolutionVerdict(outcome).ShouldBe(SupervisorResolutionVerdict.Unknown);
    }

    [Fact]
    public void A_failed_grade_marks_unverified_even_when_the_resolver_self_reported_success()
    {
        // The crown-jewel honesty case: the resolver SUCCEEDED and emitted the marker (the self-report would accept),
        // but the server grade FAILED → Unverified. This is exactly the self-report gap A3 closes.
        var outcome = ResolveOutcome("Succeeded", $"reconciled and tested {Marker}", gradePassed: false);

        SupervisorOutcome.ReadResolutionVerdict(outcome).ShouldBe(SupervisorResolutionVerdict.Unverified);
    }

    // ── The fold + read-back helpers ─────────────────────────────────────────────────

    [Fact]
    public void Folding_a_grade_re_emits_the_agent_fields_byte_intact_and_appends_the_grade()
    {
        var id = Guid.NewGuid();
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var folded = SupervisorOutcome.FoldAgentResults(staged, new[] { new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = "ok", ProducedBranch = "b" } });

        var graded = SupervisorOutcome.FoldAcceptanceGrade(folded, passed: true, detail: "tests-passed");

        graded.ShouldBe(folded[..^1] + ""","acceptanceGrade":{"passed":true,"detail":"tests-passed"}}""",
            "the existing agent fields are re-emitted byte-intact — only the new acceptanceGrade key changes the bytes (no idempotency-key drift)");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_acceptance_grade_round_trips_the_folded_passed(bool passed)
    {
        var folded = SupervisorOutcome.FoldAcceptanceGrade(ResolveOutcome("Succeeded", "ok", null), passed, "d");

        SupervisorOutcome.ReadAcceptanceGradePassed(folded).ShouldBe(passed);
    }

    [Fact]
    public void Read_acceptance_grade_is_null_when_no_grade_is_folded()
    {
        SupervisorOutcome.ReadAcceptanceGradePassed(ResolveOutcome("Succeeded", "ok", null)).ShouldBeNull("no acceptanceGrade field → the once-guard lets the grade run, and the verdict falls back to the marker");
        SupervisorOutcome.ReadAcceptanceGradePassed(null).ShouldBeNull();
        SupervisorOutcome.ReadAcceptanceGradePassed("not json").ShouldBeNull();
    }

    [Fact]
    public void The_acceptance_grade_timeout_is_pinned()
    {
        SupervisorLane.AcceptanceGradeTimeoutSeconds.ShouldBe(120);
    }

    // ── AppendAcceptanceGrade: the GENERIC additive fold for a terminal STOP (preserves the stop shape) ──

    [Fact]
    public void Append_acceptance_grade_preserves_the_stop_outcome_shape_and_appends_the_grade()
    {
        // Unlike the resolve-shaped FoldAcceptanceGrade, the stop fold must NOT drop the stop's own keys. Every
        // existing key is re-emitted verbatim in order and only the acceptanceGrade key is added (no shape corruption).
        var stopOutcome = JsonSerializer.Serialize(new { stopped = true, outcome = "completed", summary = "done" }, AgentJson.Options);

        var graded = SupervisorOutcome.AppendAcceptanceGrade(stopOutcome, passed: false, detail: "model-check-failed");

        graded.ShouldBe(stopOutcome[..^1] + ""","acceptanceGrade":{"passed":false,"detail":"model-check-failed"}}""",
            "the stop's stopped/outcome/summary survive byte-intact and only the acceptanceGrade key is appended");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Append_then_read_round_trips_the_passed_on_a_stop_shape(bool passed)
    {
        var stopOutcome = JsonSerializer.Serialize(new { stopped = true, outcome = "completed", summary = "done" }, AgentJson.Options);

        SupervisorOutcome.ReadAcceptanceGradePassed(SupervisorOutcome.AppendAcceptanceGrade(stopOutcome, passed, "d")).ShouldBe(passed);
    }

    [Fact]
    public void Append_acceptance_grade_starts_from_an_empty_object_for_a_blank_or_unparseable_outcome()
    {
        // Defensive (a stop outcome is always a valid object) — a null/blank/garbage input yields a bare grade object.
        SupervisorOutcome.AppendAcceptanceGrade(null, true, "d").ShouldBe("""{"acceptanceGrade":{"passed":true,"detail":"d"}}""");
        SupervisorOutcome.AppendAcceptanceGrade("not json", true, "d").ShouldBe("""{"acceptanceGrade":{"passed":true,"detail":"d"}}""");
    }

    private static string ResolveOutcome(string status, string summary, bool? gradePassed)
    {
        var agentResults = new[] { new { agentRunId = Guid.NewGuid(), status, summary, producedBranch = "b" } };

        return gradePassed is null
            ? JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults }, AgentJson.Options)
            : JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults, acceptanceGrade = new { passed = gradePassed.Value, detail = "d" } }, AgentJson.Options);
    }
}
