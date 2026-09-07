using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
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
    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>> NoManifests = new Dictionary<Guid, IReadOnlyList<PublishManifest>>();

    private static SessionSummarizer.TurnRow Turn(int n, string status, string? goal, string? result, string? branch = null) =>
        new(Guid.NewGuid(), n, status, goal, result, branch);

    private static PublishManifest PushedManifest(string branch) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, Branch = branch,
        PublishStateValue = PublishState.Pushed, RepositoryId = Guid.NewGuid(), RepositoryAlias = "primary",
    };

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

    [Fact]
    public void Parse_skips_a_null_array_element_instead_of_letting_it_reach_a_callers_Turn_access()
    {
        // JsonSerializer deserializes a JSON null into a List<T> element as a plain C# null WITHOUT throwing (there is
        // no `required`-member check against a JSON null) — a caller that assumes a clean list, e.g.
        // SessionSummarizer's `.ToDictionary(b => b.Turn)`, would NullReferenceException on this entry otherwise.
        const string json = """[null,{"turn":1,"effectiveRunId":"11111111-1111-1111-1111-111111111111","resultFingerprint":"x"}]""";

        var parsed = Should.NotThrow(() => SessionSummarySourceBindings.Parse(json));

        parsed.Count.ShouldBe(1);
        parsed[0].Turn.ShouldBe(1);
    }

    [Fact]
    public void Parse_deduplicates_a_malformed_duplicate_turn_key_instead_of_letting_a_callers_ToDictionary_throw()
    {
        const string json = """
            [
                {"turn":1,"effectiveRunId":"11111111-1111-1111-1111-111111111111","resultFingerprint":"a"},
                {"turn":1,"effectiveRunId":"22222222-2222-2222-2222-222222222222","resultFingerprint":"b"}
            ]
            """;

        var parsed = Should.NotThrow(() => SessionSummarySourceBindings.Parse(json));

        parsed.Count.ShouldBe(1, "a hand-edited duplicate 'turn' key must not reach a caller's ToDictionary(b => b.Turn) unresolved");
        Should.NotThrow(() => parsed.ToDictionary(b => b.Turn));
    }

    // ─── BuildBinding fingerprint ───────────────────────────────────────────

    [Fact]
    public void BuildBinding_fingerprint_is_deterministic_for_the_same_content()
    {
        var turn = Turn(1, "Success", "goal-1", "result-1", "main");
        var noAssessments = new Dictionary<Guid, Guid>();

        var first = SessionSummarizer.BuildBinding(turn, noAssessments, NoManifests);
        var second = SessionSummarizer.BuildBinding(turn, noAssessments, NoManifests);

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

        var a = SessionSummarizer.BuildBinding(new SessionSummarizer.TurnRow(Guid.NewGuid(), 1, statusA, goalA, resultA, branchA), noAssessments, NoManifests);
        var b = SessionSummarizer.BuildBinding(new SessionSummarizer.TurnRow(Guid.NewGuid(), 1, statusB, goalB, resultB, branchB), noAssessments, NoManifests);

        a.ResultFingerprint.ShouldNotBe(b.ResultFingerprint);
    }

    [Fact]
    public void BuildBinding_fingerprint_uses_the_manifest_resolved_branch_over_the_raw_legacy_one()
    {
        // The fold (SessionSummarizer.BuildUserPrompt) folds the manifest-preferred (I2) branch, not the raw legacy
        // OutputsJson.branch leaf, when a PublishManifest row resolves one — the fingerprint must track the SAME
        // value, or a manifest that resolves (or changes) after the fold goes undetected as drift.
        var turn = Turn(1, "Success", "goal-1", "result-1", branch: "raw-legacy-guess");
        var noAssessments = new Dictionary<Guid, Guid>();
        var withManifest = new Dictionary<Guid, IReadOnlyList<PublishManifest>> { [turn.Id] = [PushedManifest("manifest-preferred")] };

        var boundToManifest = SessionSummarizer.BuildBinding(turn, noAssessments, withManifest);
        var boundToLegacyOnly = SessionSummarizer.BuildBinding(turn, noAssessments, NoManifests);

        boundToManifest.ResultFingerprint.ShouldNotBe(boundToLegacyOnly.ResultFingerprint,
            "a resolvable manifest branch must drive the fingerprint — a fingerprint still keyed on the raw legacy leaf would miss this turn's real content");
    }

    [Fact]
    public void BuildBinding_fingerprint_ignores_the_legacy_branch_once_a_manifest_resolves()
    {
        var noAssessments = new Dictionary<Guid, Guid>();
        var runId = Guid.NewGuid();
        var withLegacyA = new SessionSummarizer.TurnRow(runId, 1, "Success", "goal-1", "result-1", "legacy-a");
        var withLegacyB = new SessionSummarizer.TurnRow(runId, 1, "Success", "goal-1", "result-1", "legacy-b");
        var manifests = new Dictionary<Guid, IReadOnlyList<PublishManifest>> { [runId] = [PushedManifest("manifest-preferred")] };

        var a = SessionSummarizer.BuildBinding(withLegacyA, noAssessments, manifests);
        var b = SessionSummarizer.BuildBinding(withLegacyB, noAssessments, manifests);

        a.ResultFingerprint.ShouldBe(b.ResultFingerprint, "once a manifest resolves the branch, the superseded raw legacy leaf must no longer affect the fingerprint");
    }

    [Fact]
    public void BuildBinding_captures_the_runs_latest_assessment_id_when_one_is_recorded()
    {
        var turn = Turn(1, "Success", "goal-1", "result-1");
        var assessmentId = Guid.NewGuid();

        var binding = SessionSummarizer.BuildBinding(turn, new Dictionary<Guid, Guid> { [turn.Id] = assessmentId }, NoManifests);

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
