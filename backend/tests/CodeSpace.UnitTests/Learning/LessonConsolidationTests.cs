using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Learning;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

/// <summary>
/// 🟢 Unit: D1's consolidation fold — the anti-confabulation law. Pins: an add citing only shown runs mints a
/// lesson carrying mode/repo/citations; a citation outside the shown set is REFUSED whole (never partially
/// honored); an uncited lesson is refused; update/invalidate must name a CURRENT lesson id verbatim (a
/// hallucinated id mints nothing); update merges texts and unions citations; invalidation is one-way; the op
/// vocabulary is closed. Plus the prompt pin: lesson ids and run ids appear verbatim (they are the only lawful
/// reference space the brain may cite from).
/// </summary>
[Trait("Category", "Unit")]
public class LessonConsolidationTests
{
    private static readonly Guid RunA = Guid.NewGuid();
    private static readonly Guid RunB = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Repo = Guid.NewGuid();

    [Fact]
    public void An_add_citing_shown_runs_mints_a_lesson_with_provenance()
    {
        var fold = Apply(Proposal("add", sourceRunIds: [RunA.ToString(), RunB.ToString()]));

        var lesson = fold.Inserts.ShouldHaveSingleItem();
        lesson.SourceRunIds.ShouldBe(new[] { RunA, RunB }, ignoreOrder: true);
        lesson.Mode.ShouldBe("supervisor", "the lesson inherits the cited runs' mode — D2's retrieval key");
        lesson.RepositoryId.ShouldBe(Repo, "every cited run scoped to the same repo — the lesson keeps the sharper key");
        lesson.TeamId.ShouldBe(Team);
        lesson.DistilledByModel.ShouldBe("test-model");
        lesson.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public void A_citation_outside_the_shown_runs_refuses_the_whole_proposal()
    {
        var fold = Apply(Proposal("add", sourceRunIds: [RunA.ToString(), Guid.NewGuid().ToString()]));

        fold.Inserts.ShouldBeEmpty("one hallucinated citation poisons the proposal — refused whole, never partially honored");
        fold.Rejections.ShouldHaveSingleItem().ShouldContain("not a run this round showed");
    }

    [Fact]
    public void An_uncited_lesson_is_no_lesson()
    {
        var fold = Apply(Proposal("add", sourceRunIds: []));

        fold.Inserts.ShouldBeEmpty();
        fold.Rejections.ShouldHaveSingleItem().ShouldContain("no citations");
    }

    [Fact]
    public void An_update_naming_a_hallucinated_id_mints_nothing()
    {
        var existing = ExistingLesson();
        var fold = Apply(Proposal("update", existingLessonId: Guid.NewGuid().ToString(), sourceRunIds: [RunA.ToString()]), existing);

        fold.Updates.ShouldBe(0);
        fold.Rejections.ShouldHaveSingleItem().ShouldContain("names no CURRENT lesson");
        existing.HowToApply.ShouldBe("old advice", "the tracked row must be untouched");
    }

    [Fact]
    public void An_update_merges_texts_and_unions_citations()
    {
        var existing = ExistingLesson();
        var fold = Apply(Proposal("update", existingLessonId: existing.Id.ToString(), howToApply: "new advice", sourceRunIds: [RunA.ToString()]), existing);

        fold.Updates.ShouldBe(1);
        existing.HowToApply.ShouldBe("new advice");
        existing.SourceRunIds.ShouldBe(new[] { RunB, RunA }, ignoreOrder: true, customMessage: "citations UNION — provenance only ever grows");
    }

    [Fact]
    public void Invalidation_is_one_way_and_needs_the_verbatim_id()
    {
        var existing = ExistingLesson();
        var fold = Apply(Proposal("invalidate", existingLessonId: existing.Id.ToString(), sourceRunIds: []), existing);

        fold.Invalidations.ShouldBe(1);
        existing.InvalidatedAt.ShouldNotBeNull("temporal retirement — readers stop seeing it; the row itself is never rewritten");
    }

    [Fact]
    public void The_op_vocabulary_is_closed()
    {
        var fold = Apply(Proposal("promote", sourceRunIds: [RunA.ToString()]));

        fold.Inserts.ShouldBeEmpty();
        fold.Rejections.ShouldHaveSingleItem().ShouldContain("unknown action");
    }

    [Fact]
    public void The_prompt_names_lesson_ids_and_run_ids_verbatim()
    {
        var existing = ExistingLesson();
        var prompt = LessonDistiller.BuildUserPrompt(new[] { existing }, Candidates().Values);

        prompt.ShouldContain($"id={existing.Id}", customMessage: "update/invalidate must be able to name the id verbatim");
        prompt.ShouldContain($"run {RunA}", customMessage: "citations can only come from ids the prompt actually showed");
        prompt.ShouldContain("boom: exit 2", customMessage: "the error text is the raw material of the post-mortem");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private static LessonFold Apply(LessonProposal proposal, params Lesson[] current) =>
        LessonConsolidation.Apply(current, new LessonProposals { Lessons = [proposal] }, Candidates(), Team, "test-model", DateTimeOffset.UtcNow);

    private static Dictionary<Guid, CandidateRun> Candidates() => new()
    {
        [RunA] = new CandidateRun(RunA, "supervisor", Repo, "Failure", "boom: exit 2", ["1. spawn [Succeeded]"]),
        [RunB] = new CandidateRun(RunB, "supervisor", Repo, "Parked", null, []),
    };

    private static LessonProposal Proposal(string action, string? existingLessonId = null, string howToApply = "advice", List<string>? sourceRunIds = null) => new()
    {
        Action = action,
        ExistingLessonId = existingLessonId,
        FailureClass = "broken-acceptance-command",
        WhatFailed = "w",
        Why = "y",
        HowToApply = howToApply,
        SourceRunIds = sourceRunIds ?? [],
    };

    private static Lesson ExistingLesson() => new()
    {
        Id = Guid.NewGuid(), TeamId = Team, Mode = "supervisor", FailureClass = "stale", WhatFailed = "old", Why = "old", HowToApply = "old advice",
        SourceRunIds = [RunB], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow.AddDays(-3),
    };
}
