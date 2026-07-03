using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Sessions;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="IJournalProjector"/> resolved from DI, over the real session
/// skeleton + the real journal walk): the journal end-to-end. A session's turns project into a <see cref="JournalView"/>;
/// the FOCUSED turn (the run-anchored one) is walked into its chronological steps, the other turn is a light card with no
/// steps; a foreign session projects to null. Proves the whole composition — session read + timeline walk — over real data.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JournalProjectorFlowTests
{
    private readonly PostgresFixture _fixture;

    public JournalProjectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Projects_a_session_focusing_the_anchored_turn_into_its_journal_steps()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Build the dashboard");

        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "First task", resultSummary: "First task done");
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Second task", resultSummary: "Second task done");

        // Turn 1's run made two supervisor decisions — the walk turns them into chronological steps.
        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t);
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        view.ShouldNotBeNull();
        view!.Turns.Count.ShouldBe(2);
        view.AnchorTurnIndex.ShouldBe(1, "entering by turn 1's run anchors turn 1");

        var focused = view.Turns.Single(t => t.Focused);
        focused.TurnIndex.ShouldBe(1);
        focused.UserMessage.ShouldBe("First task");
        focused.Steps.Select(s => s.Kind).ShouldBe(new[] { JournalStepKinds.Decision, JournalStepKinds.Decision }, "the focused turn is walked into its decision steps, chronologically");
        focused.Steps.Select(s => s.Title).ShouldBe(new[] { "Supervisor planned the work", "Supervisor stopped" });
        view.Cursor.ShouldBe(focused.Steps[^1].Cursor, "the view cursor is the focused turn's newest step");

        var collapsed = view.Turns.Single(t => !t.Focused);
        collapsed.TurnIndex.ShouldBe(2);
        collapsed.Steps.ShouldBeEmpty("the non-focused turn is a light card");
        collapsed.Summary.ShouldBe("Second task done");
    }

    [Fact]
    public async Task Entering_by_a_prior_attempts_run_focuses_that_attempts_flow_not_the_latest()
    {
        // Parity with the room's attempt switcher, over a REAL rerun ladder: turn 1 was reran (attempt 1 failed,
        // attempt 2 succeeded). Deep-linking attempt 1's run must focus attempt 1 — its Failure status + its own walked
        // steps — not the newest attempt's Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reran turn");

        var t = DateTimeOffset.UtcNow;
        var attempt1 = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Failure, createdAt: t);
        await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: attempt1, WorkflowRunStatus.Success, createdAt: t.AddMinutes(1));

        // attempt 1 made a decision before it failed — the walk of attempt 1 surfaces it.
        await SeedDecisionAsync(attempt1, teamId, SupervisorDecisionKinds.Plan, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(attempt1, teamId, CancellationToken.None);

        view.ShouldNotBeNull();
        var focused = view!.Turns.Single(t => t.Focused);
        focused.RunId.ShouldBe(attempt1, "the anchored attempt is focused, not the newest");
        focused.Status.ShouldBe(WorkflowRunStatus.Failure, "attempt 1's OWN status, not attempt 2's Success");
        focused.Steps.Select(s => s.Title).ShouldBe(new[] { "Supervisor planned the work" }, "attempt 1's run was walked");
    }

    [Fact]
    public async Task A_mid_stream_since_returns_only_the_steps_after_the_cursor()
    {
        // The delta over REAL walk cursors through the full handler pipeline: three decisions → three steps; a re-read
        // with ?since = the FIRST step's cursor returns only the LATER two, and preserves the self-heal StepCount.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Live");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Task", resultSummary: "done");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t);
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Merge, t.AddSeconds(1));
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(2));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();

        var full = (await mediator.Send(new GetRunJournalQuery { RunId = run1 }))!;
        var steps = full.Turns.Single(t => t.Focused).Steps;
        steps.Count.ShouldBe(3, "three decisions → three steps");

        var delta = (await mediator.Send(new GetRunJournalQuery { RunId = run1, Since = steps[0].Cursor }))!;
        var focused = delta.Turns.Single(t => t.Focused);

        focused.Steps.Select(s => s.Id).ShouldBe(steps.Skip(1).Select(s => s.Id), "only the steps after the client's cursor come back over the real pipeline");
        focused.StepCount.ShouldBe(3, "the delta preserves the full step total (the self-heal signal)");
    }

    [Fact]
    public async Task A_decision_step_carries_the_supervisors_authored_rationale()
    {
        // The facts-enrichment path end-to-end over real data: a decision whose payload carries a rationale surfaces it on
        // its journal step (the chain-of-thought), read off the durable decision tape by the real facts source — NOT from
        // the timeline event. A decision with no rationale stays bare.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reasoned run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Task", resultSummary: "done");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t, RationalePayload("Break the work into 3 tasks", "the goal names 3 files"));
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var steps = view!.Turns.Single(t => t.Focused).Steps;
        steps[0].Title.ShouldBe("Supervisor planned the work");
        steps[0].Rationale.ShouldBe("Break the work into 3 tasks · Evidence: the goal names 3 files", "the plan step carries the model's authored rationale, read off the decision payload");
        steps[1].Rationale.ShouldBeNull("the stop authored no rationale — it stays bare");
    }

    [Fact]
    public async Task A_foreign_session_projects_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        (await scope.Resolve<IJournalProjector>().ProjectByRunAsync(Guid.NewGuid(), teamId, CancellationToken.None))
            .ShouldBeNull("a run that isn't the team's is null — no existence leak");
    }

    private static string RationalePayload(string why, string evidence) =>
        JsonSerializer.Serialize(new { rationale = new { why, evidence } });

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, string kind, DateTimeOffset at, string payloadJson = "{}")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = null,
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAttemptAsync(Guid teamId, Guid sessionId, int turnIndex, Guid? rootRunId, WorkflowRunStatus status, DateTimeOffset createdAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId,
            OutputsJson = "{}", CreatedDate = createdAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId, string title)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTurnAsync(Guid teamId, Guid sessionId, int turn, string goal, string? resultSummary)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var outputs = string.IsNullOrEmpty(resultSummary) ? "{}" : JsonSerializer.Serialize(new { summary = resultSummary, branch = (string?)null });

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputs,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }
}
