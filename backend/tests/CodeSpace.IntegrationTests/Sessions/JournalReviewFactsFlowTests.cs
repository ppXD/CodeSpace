using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 The journal's adversarial-exchange facts against REAL Postgres rows: reviewer agent runs (the S8 <c>#review</c> /
/// D① <c>#plan-review</c> suffix keys) are discovered by the reader, their durable <c>VERDICT:</c> results parse into
/// render-ready verdicts keyed to the exact verdict-beat event, and the producer-card join strips the suffix back to
/// the producer's own key — latest round wins. Team scoping is fail-closed (a foreign team reads nothing).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JournalReviewFactsFlowTests
{
    private readonly PostgresFixture _fixture;

    public JournalReviewFactsFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Reviewer_verdicts_land_on_the_verdict_beat_and_the_producer_card_join()
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = SeedTeam(db);
        await db.SaveChangesAsync();   // the team FIRST — agent_run's team FK is a DB constraint EF has no navigation for

        var producerId = SeedAgentRun(db, teamId, runId, "map#0", AgentRunStatus.Succeeded, resultSummary: null);

        // Round 1's reviewer flagged; round 2's approved — the card join must show the LATEST (the approval).
        var reviewer1 = SeedAgentRun(db, teamId, runId, "map#0#review", AgentRunStatus.Succeeded,
            resultSummary: """Reviewed. VERDICT: {"approved": false, "rationale": "placeholder hack", "issues": [{"issue": "hack committed", "evidence": "feature.txt line 1", "severity": "blocker"}]}""",
            harness: "claude-code", createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var reviewer2 = SeedAgentRun(db, teamId, runId, "map#0#review", AgentRunStatus.Succeeded,
            resultSummary: """Reviewed again. VERDICT: {"approved": true, "rationale": "clean now", "issues": []}""",
            harness: "claude-code", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        var planReviewer = SeedAgentRun(db, teamId, runId, "#plan-review", AgentRunStatus.Succeeded,
            resultSummary: """VERDICT: {"approved": false, "rationale": "the plan schedules finished work", "issues": [{"issue": "step 2 already done", "evidence": "src/auth.cs already validates", "severity": "blocker"}]}""",
            harness: "codex-cli");

        await db.SaveChangesAsync();

        var reader = scope.Resolve<ReviewerVerdictReader>();

        // The verdict-beat facts key by the SYNTHETIC verdict event's deterministic id — read off the durable result,
        // so NO final-summary event is seeded here on purpose: a codex-cli reviewer emits none, and the beat must
        // surface for every harness alike (the regression that hid a real run's verdict).
        var facts = await scope.Resolve<ReviewVerdictFactsSource>().GatherAsync(runId, teamId, CancellationToken.None);

        facts.Count.ShouldBe(3, "every landed reviewer verdict becomes beat facts — both rounds AND the plan review");
        var flagged = facts[ReviewVerdictTimelineMap.EventId(reviewer1)].Review!;
        flagged.Approved.ShouldBeFalse();
        flagged.Rationale.ShouldBe("placeholder hack");
        flagged.Issues.ShouldContain("hack committed (evidence: feature.txt line 1)", "the evidence-attached issue renders S8a-style");
        flagged.ReviewerRunId.ShouldBe(reviewer1);
        flagged.ReviewerHarness.ShouldBe("claude-code");
        flagged.Scope.ShouldBe(JournalReviewVerdict.OutputScope);

        facts[ReviewVerdictTimelineMap.EventId(reviewer2)].Review!.Approved.ShouldBeTrue();

        var plan = facts[ReviewVerdictTimelineMap.EventId(planReviewer)].Review!;
        plan.Scope.ShouldBe(JournalReviewVerdict.PlanScope);
        plan.Approved.ShouldBeFalse();

        // The producer-card join: suffix-strip back to the producer's key, LATEST round wins.
        var producerKeys = await reader.ProducerKeysAsync(new[] { producerId }, teamId, CancellationToken.None);
        producerKeys[producerId].ShouldBe("map#0");

        var rows = await reader.ReadForRunAsync(runId, teamId, CancellationToken.None);
        var latest = rows.Where(r => r.IterationKey == "map#0#review").OrderByDescending(r => r.CreatedAt).First();
        latest.Verdict!.Approved.ShouldBeTrue("the card shows the LATEST verdict — the exchange healed");

        // Team scoping: a foreign team reads nothing (fail-closed, like every projection).
        (await reader.ReadForRunAsync(runId, Guid.NewGuid(), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_in_flight_reviewer_keeps_a_verdict_less_beat_and_an_off_contract_one_contributes_nothing()
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = SeedTeam(db);
        await db.SaveChangesAsync();   // the team FIRST — agent_run's team FK is a DB constraint EF has no navigation for

        var inspecting = SeedAgentRun(db, teamId, runId, "map#0#review", AgentRunStatus.Running, resultSummary: null);                 // still reviewing
        SeedAgentRun(db, teamId, runId, "map#1#review", AgentRunStatus.Succeeded, resultSummary: "I looked around, seems fine.");      // off-contract — no VERDICT marker
        SeedAgentRun(db, teamId, runId, "map#2#review", AgentRunStatus.Failed, resultSummary: null);                                   // terminal non-success — the model-critic fallback's beat takes over

        await db.SaveChangesAsync();

        // J6: the exchange never goes silent while a grounded review runs — the in-flight reviewer holds a
        // verdict-LESS row whose beat reads "inspecting…" and UPGRADES IN PLACE (same event id) when it lands.
        var rows = await scope.Resolve<ReviewerVerdictReader>().ReadForRunAsync(runId, teamId, CancellationToken.None);

        var row = rows.ShouldHaveSingleItem("in-flight ⇒ a live beat; off-contract / failed ⇒ nothing is fabricated");
        row.ReviewerRunId.ShouldBe(inspecting);
        row.Verdict.ShouldBeNull("no verdict yet — the beat is the live placeholder");

        var beat = ReviewVerdictTimelineMap.ToEvent(row);
        beat.Id.ShouldBe(ReviewVerdictTimelineMap.EventId(inspecting), "the SAME id the landed verdict will carry — the upgrade is in place, never a second beat");
        beat.Title.ShouldBe("Independent reviewer is inspecting the produced work…");
        beat.Severity.ShouldBe(CodeSpace.Messages.Tasks.Timeline.TimelineSeverity.Info);

        // The verdict-card facts skip it — the beat renders bare until the verdict lands.
        (await scope.Resolve<ReviewVerdictFactsSource>().GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("no card until a verdict exists");
    }

    [Fact]
    public async Task A_folded_agent_verdict_skips_the_synthetic_beat_but_its_draft_lands_on_the_decision_step()
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = SeedTeam(db);
        await db.SaveChangesAsync();

        // The H1/J4 fold: an AGENT verdict (ViaAgent, carrying the discarded draft's attribution) + the ladder's
        // later MODEL verdict, both riding the surviving decision's outcome.
        var reviews = new[]
        {
            new SupervisorDecisionReview { Approved = false, Rationale = "misses the migration", Issues = new[] { "no rollback (evidence: none)" }, Scope = "plan", DraftAttribution = "plan draft · authored via m1 · 8,200 tokens", ViaAgent = true },
            new SupervisorDecisionReview { Approved = true, Rationale = "revision holds", Scope = "plan" },
        };

        var decision = new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = $"plan:{Guid.NewGuid():N}",
            InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = Core.Services.Supervisor.SupervisorOutcome.WriteReviews("""{"outcome":"planned"}""", reviews),
        };
        db.SupervisorDecisionRecord.Add(decision);
        await db.SaveChangesAsync();

        // The timeline emits a synthetic beat ONLY for the model verdict — the agent verdict's reviewer run is its
        // own beat already (the ReviewVerdictTimelineSource's), so beating the fold too would show the review twice.
        var events = await scope.Resolve<DecisionReviewTimelineSource>().ContributeAsync(
            new Messages.Tasks.Timeline.RunTimelineContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        events.Count.ShouldBe(1, "one beat: the model verdict — the ViaAgent entry is skipped");
        var beat = events[0];
        beat.Id.ShouldBe(DecisionReviewTimelineMap.EventId(decision.Id, 1));
        beat.Title.ShouldBe("Model critic approved the revised plan", "index 1 names the REVISION — the ladder reads draft → revised");

        var facts = await scope.Resolve<DecisionReviewFactsSource>().GatherAsync(runId, teamId, CancellationToken.None);

        facts[DecisionReviewTimelineMap.EventId(decision.Id, 1)].Review!.Approved.ShouldBeTrue("the model verdict's card facts land on its beat");
        facts.ContainsKey(DecisionReviewTimelineMap.EventId(decision.Id, 0)).ShouldBeFalse("no card for the ViaAgent entry — its reviewer run's beat carries the verdict");

        // J4: the DISCARDED DRAFT's attribution rides the SURVIVING DECISION's own step — the once-anonymous
        // authoring model call reads as part of the exchange, uniformly for model- AND agent-flagged drafts.
        facts[SupervisorDecisionTimelineMap.EventId(decision)].Draft
            .ShouldBe("plan draft · authored via m1 · 8,200 tokens");
    }

    private static Guid SeedTeam(CodeSpaceDbContext db)
    {
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"jrf-{userId:N}@test.local", Name = $"jrf-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"jrf-{teamId:N}", Name = "Journal Review Facts Team", Kind = TeamKind.Workspace, OwnerUserId = userId });

        return teamId;
    }

    private static Guid SeedAgentRun(CodeSpaceDbContext db, Guid teamId, Guid runId, string iterationKey, AgentRunStatus status, string? resultSummary, string harness = "codex-cli", DateTimeOffset? createdAt = null)
    {
        var id = Guid.NewGuid();

        db.AgentRun.Add(new AgentRun
        {
            Id = id,
            TeamId = teamId,
            WorkflowRunId = runId,
            NodeId = "agents",
            IterationKey = iterationKey,
            Harness = harness,
            Status = status,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = harness }, AgentJson.Options),
            ResultJson = resultSummary is null ? null : JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = resultSummary }, AgentJson.Options),
            CreatedDate = createdAt ?? DateTimeOffset.UtcNow,
        });

        return id;
    }

}
