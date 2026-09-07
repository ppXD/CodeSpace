using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins <see cref="SessionSummarySourceBinding"/>'s pure logic without a DB round-trip: JSON parse/serialize
/// round-tripping (and its fail-soft handling of absent/malformed legacy data), the content fingerprint
/// <see cref="SessionSummarizer.BuildBinding"/> derives, and the dirty-detection <see cref="SessionSummarizer.IsDirty"/>
/// uses to decide whether an already-folded turn's bound source changed behind the summary watermark.
/// </summary>
[Trait("Category", "Unit")]
public class SessionSummarySourceBindingTests
{
    private static SessionSummarizer.TurnRow Turn(int n, string status, string? goal, string? result, string? branch = null) =>
        new(Guid.NewGuid(), n, status, goal, result, branch);

    // ─── Parse / Serialize ──────────────────────────────────────────────────

    [Fact]
    public void Serialize_then_Parse_round_trips_every_field_including_a_null_assessment_id()
    {
        var runId = Guid.NewGuid();
        var assessmentId = Guid.NewGuid();
        var bindings = new List<SessionSummarySourceBinding>
        {
            new() { Turn = 1, EffectiveRunId = runId, ResultFingerprint = "abc123", AssessmentId = assessmentId },
            new() { Turn = 2, EffectiveRunId = Guid.NewGuid(), ResultFingerprint = "def456", AssessmentId = null },
        };

        var parsed = SessionSummarySourceBindings.Parse(SessionSummarySourceBindings.Serialize(bindings));

        parsed.Count.ShouldBe(2);
        parsed[0].ShouldBe(bindings[0]);
        parsed[1].ShouldBe(bindings[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("\"just a string\"")]
    public void Parse_of_absent_blank_or_malformed_json_is_an_empty_list_not_a_throw(string? json)
    {
        Should.NotThrow(() => SessionSummarySourceBindings.Parse(json)).ShouldBeEmpty();
    }

    // ─── BuildBinding fingerprint ───────────────────────────────────────────

    [Fact]
    public void BuildBinding_fingerprint_is_deterministic_for_the_same_content()
    {
        var turn = Turn(1, "Success", "goal-1", "result-1", "main");
        var noAssessments = new Dictionary<Guid, Guid>();

        var first = SessionSummarizer.BuildBinding(turn, noAssessments);
        var second = SessionSummarizer.BuildBinding(turn, noAssessments);

        first.ResultFingerprint.ShouldBe(second.ResultFingerprint);
        first.EffectiveRunId.ShouldBe(turn.Id);
        first.Turn.ShouldBe(1);
        first.AssessmentId.ShouldBeNull();
    }

    [Theory]
    [InlineData("Success", "goal-1", "result-1", "main", "Success", "goal-1", "result-2", "main")]      // result changed
    [InlineData("Success", "goal-1", "result-1", "main", "Success", "goal-2", "result-1", "main")]      // goal changed
    [InlineData("Success", "goal-1", "result-1", "main", "Success", "goal-1", "result-1", "other")]     // branch changed
    [InlineData("Success", "goal-1", "result-1", "main", "Failure", "goal-1", "result-1", "main")]      // status changed
    public void BuildBinding_fingerprint_changes_whenever_the_folded_content_would_change(
        string statusA, string goalA, string resultA, string branchA,
        string statusB, string goalB, string resultB, string branchB)
    {
        var noAssessments = new Dictionary<Guid, Guid>();

        var a = SessionSummarizer.BuildBinding(new SessionSummarizer.TurnRow(Guid.NewGuid(), 1, statusA, goalA, resultA, branchA), noAssessments);
        var b = SessionSummarizer.BuildBinding(new SessionSummarizer.TurnRow(Guid.NewGuid(), 1, statusB, goalB, resultB, branchB), noAssessments);

        a.ResultFingerprint.ShouldNotBe(b.ResultFingerprint);
    }

    [Fact]
    public void BuildBinding_captures_the_runs_latest_assessment_id_when_one_is_recorded()
    {
        var turn = Turn(1, "Success", "goal-1", "result-1");
        var assessmentId = Guid.NewGuid();

        var binding = SessionSummarizer.BuildBinding(turn, new Dictionary<Guid, Guid> { [turn.Id] = assessmentId });

        binding.AssessmentId.ShouldBe(assessmentId);
    }

    // ─── IsDirty ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsDirty_is_false_when_there_is_no_stored_binding()
    {
        var fresh = new SessionSummarySourceBinding { Turn = 1, EffectiveRunId = Guid.NewGuid(), ResultFingerprint = "x" };

        SessionSummarizer.IsDirty(fresh, stored: null).ShouldBeFalse("a legacy turn with no recorded binding has unknown provenance — never a forced refresh");
    }

    [Fact]
    public void IsDirty_is_false_when_fresh_matches_stored_exactly()
    {
        var runId = Guid.NewGuid();
        var assessmentId = Guid.NewGuid();
        var fresh = new SessionSummarySourceBinding { Turn = 1, EffectiveRunId = runId, ResultFingerprint = "x", AssessmentId = assessmentId };
        var stored = fresh with { };

        SessionSummarizer.IsDirty(fresh, stored).ShouldBeFalse();
    }

    [Fact]
    public void IsDirty_is_true_when_the_effective_run_id_changed()
    {
        var stored = new SessionSummarySourceBinding { Turn = 1, EffectiveRunId = Guid.NewGuid(), ResultFingerprint = "x" };
        var fresh = stored with { EffectiveRunId = Guid.NewGuid() };

        SessionSummarizer.IsDirty(fresh, stored).ShouldBeTrue("a rerun won since the fold — the effective attempt itself changed");
    }

    [Fact]
    public void IsDirty_is_true_when_the_result_fingerprint_changed()
    {
        var stored = new SessionSummarySourceBinding { Turn = 1, EffectiveRunId = Guid.NewGuid(), ResultFingerprint = "x" };
        var fresh = stored with { ResultFingerprint = "y" };

        SessionSummarizer.IsDirty(fresh, stored).ShouldBeTrue("the same run's content mutated underneath the summary");
    }

    [Fact]
    public void IsDirty_is_true_when_an_assessment_appeared_that_did_not_exist_at_fold_time()
    {
        var stored = new SessionSummarySourceBinding { Turn = 1, EffectiveRunId = Guid.NewGuid(), ResultFingerprint = "x", AssessmentId = null };
        var fresh = stored with { AssessmentId = Guid.NewGuid() };

        SessionSummarizer.IsDirty(fresh, stored).ShouldBeTrue("a completion assessment was recorded after the fold — the summary's carried evidence is now behind");
    }
}
