using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
            resultSummary: """Reviewed. VERDICT: {"approved": false, "rationale": "placeholder hack", "issues": [{"issue": "hack committed", "evidence": "feature.txt line 1"}]}""",
            harness: "claude-code", createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var reviewer2 = SeedAgentRun(db, teamId, runId, "map#0#review", AgentRunStatus.Succeeded,
            resultSummary: """Reviewed again. VERDICT: {"approved": true, "rationale": "clean now", "issues": []}""",
            harness: "claude-code", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));

        var planReviewer = SeedAgentRun(db, teamId, runId, "#plan-review", AgentRunStatus.Succeeded,
            resultSummary: """VERDICT: {"approved": false, "rationale": "the plan schedules finished work", "issues": [{"issue": "step 2 already done", "evidence": "src/auth.cs already validates"}]}""",
            harness: "codex-cli");

        var flaggedBeatEvent = SeedFinalSummary(db, reviewer1);
        var approvedBeatEvent = SeedFinalSummary(db, reviewer2);
        var planBeatEvent = SeedFinalSummary(db, planReviewer);

        await db.SaveChangesAsync();

        var reader = scope.Resolve<ReviewerVerdictReader>();

        // The verdict-beat facts: every landed verdict keys to ITS OWN final-summary event.
        var facts = await scope.Resolve<ReviewVerdictFactsSource>().GatherAsync(runId, teamId, CancellationToken.None);

        facts.Count.ShouldBe(3, "every landed reviewer verdict becomes beat facts — both rounds AND the plan review");
        var flagged = facts[AgentEventTimelineMap.EventId(flaggedBeatEvent)].Review!;
        flagged.Approved.ShouldBeFalse();
        flagged.Rationale.ShouldBe("placeholder hack");
        flagged.Issues.ShouldContain("hack committed (evidence: feature.txt line 1)", "the evidence-attached issue renders S8a-style");
        flagged.ReviewerRunId.ShouldBe(reviewer1);
        flagged.ReviewerHarness.ShouldBe("claude-code");
        flagged.Scope.ShouldBe(JournalReviewVerdict.OutputScope);

        facts[AgentEventTimelineMap.EventId(approvedBeatEvent)].Review!.Approved.ShouldBeTrue();

        var plan = facts[AgentEventTimelineMap.EventId(planBeatEvent)].Review!;
        plan.Scope.ShouldBe(JournalReviewVerdict.PlanScope);
        plan.Approved.ShouldBeFalse();

        // The producer-card join: suffix-strip back to the producer's key, LATEST round wins.
        var producerKeys = await reader.ProducerKeysAsync(new[] { producerId }, teamId, CancellationToken.None);
        producerKeys[producerId].ShouldBe("map#0");

        var rows = await reader.ReadForRunAsync(runId, teamId, CancellationToken.None);
        var latest = rows.Where(r => r.IterationKey == "map#0#review").OrderByDescending(r => r.CreatedAt).First();
        latest.Verdict.Approved.ShouldBeTrue("the card shows the LATEST verdict — the exchange healed");

        // Team scoping: a foreign team reads nothing (fail-closed, like every projection).
        (await reader.ReadForRunAsync(runId, Guid.NewGuid(), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_in_flight_or_off_contract_reviewer_contributes_no_verdict()
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = SeedTeam(db);
        await db.SaveChangesAsync();   // the team FIRST — agent_run's team FK is a DB constraint EF has no navigation for

        SeedAgentRun(db, teamId, runId, "map#0#review", AgentRunStatus.Running, resultSummary: null);                                  // still reviewing
        SeedAgentRun(db, teamId, runId, "map#1#review", AgentRunStatus.Succeeded, resultSummary: "I looked around, seems fine.");      // off-contract — no VERDICT marker

        await db.SaveChangesAsync();

        (await scope.Resolve<ReviewerVerdictReader>().ReadForRunAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("no landed in-contract verdict ⇒ no facts — the bare review steps stand, nothing is fabricated");
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

    private static Guid SeedFinalSummary(CodeSpaceDbContext db, Guid agentRunId)
    {
        var id = Guid.NewGuid();

        db.AgentRunEvent.Add(new AgentRunEvent { Id = id, AgentRunId = agentRunId, Sequence = 1, Kind = AgentEventKind.FinalSummary, Text = "final", OccurredAt = DateTimeOffset.UtcNow });

        return id;
    }
}
