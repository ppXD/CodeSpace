using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// S5 — a fork (replay / rerun / rerun-map) RIDES the parent's session: it INHERITS the parent run's SessionId with a
/// NULL turn index (Correction-1 — a derived run attaches to the thread via ParentRunId, consuming no new turn), so
/// replaying a turn keeps it ON the thread instead of orphaning it. Before S5 a fork dropped the session
/// (<c>session: null</c>). Proven through the REAL <see cref="IWorkflowService.ReplayRunAsync"/> over both fork branches
/// (snapshot inline-def + authored re-pinned-version), plus the session-less byte-identical case. The fork is staged
/// but not executed (the inheritance is established at staging — AutoExecute paused).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionForkInheritFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionForkInheritFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Replay_of_a_session_bound_snapshot_run_inherits_the_session_with_no_new_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var originalRunId = await SeedSnapshotRunAsync(teamId, sessionId, turnIndex: 1);

        using var pause = PauseAutoExecute();
        var replayRunId = await ReplayAsync(originalRunId, teamId, userId);

        var fork = await LoadRunAsync(replayRunId);
        fork.SessionId.ShouldBe(sessionId, "the replay RIDES the parent's thread — inherits its SessionId");
        fork.SessionTurnIndex.ShouldBeNull("a fork consumes NO new turn (attaches via ParentRunId, Correction-1)");
        fork.ParentRunId.ShouldBe(originalRunId, "fork lineage");
    }

    [Fact]
    public async Task Replay_of_a_session_less_run_stays_session_less_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalRunId = await SeedSnapshotRunAsync(teamId, sessionId: null, turnIndex: null);

        using var pause = PauseAutoExecute();
        var replayRunId = await ReplayAsync(originalRunId, teamId, userId);

        var fork = await LoadRunAsync(replayRunId);
        fork.SessionId.ShouldBeNull("a session-less parent ⇒ a session-less fork (byte-identical to the pre-S5 behaviour)");
        fork.SessionTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task Replay_of_a_session_bound_authored_run_inherits_the_session()
    {
        // The other StageForkedRunAsync branch (authored re-pinned-version, via RunStarter + RunSourceEnvelope.Session).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        var workflowId = await CreateEchoWorkflowAsync(teamId, userId);
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);   // completes the original (stamps ReleaseHashAtRun the authored fork re-pins)
        await BindRunToSessionAsync(originalRunId, sessionId, turnIndex: 1);   // make the authored run a session turn

        using var pause = PauseAutoExecute();
        var replayRunId = await ReplayAsync(originalRunId, teamId, userId);

        var fork = await LoadRunAsync(replayRunId);
        fork.WorkflowId.ShouldBe(workflowId, "an authored replay re-pins the workflow (the authored fork branch)");
        fork.SessionId.ShouldBe(sessionId, "the authored fork branch inherits the parent's session too");
        fork.SessionTurnIndex.ShouldBeNull();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<Guid> ReplayAsync(Guid originalRunId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ReplayRunAsync(originalRunId, teamId, userId, CancellationToken.None);
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed a completed SNAPSHOT run (WorkflowId=null, inline frozen def) — optionally bound to a session as a turn — the shape a launched task leaves and a replay forks from.</summary>
    private async Task<Guid> SeedSnapshotRunAsync(Guid teamId, Guid? sessionId, int? turnIndex)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, ProjectionKind = "single-agent",
            DefinitionSnapshotJson = JsonSerializer.Serialize(WorkflowsTestSeed.MinimalDefinition()),
            DefinitionSnapshotHash = "test-hash",
            SessionId = sessionId, SessionTurnIndex = turnIndex,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> CreateEchoWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "fork-inherit-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task BindRunToSessionAsync(Guid runId, Guid sessionId, int turnIndex)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.SessionId = sessionId;
        run.SessionTurnIndex = turnIndex;
        await db.SaveChangesAsync();
    }

    private IDisposable PauseAutoExecute()
    {
        SetAutoExecute(clearFirst: true, value: false);
        return new Restore(this);
    }

    private void SetAutoExecute(bool clearFirst, bool value)
    {
        using var scope = _fixture.BeginScope();
        var jobClient = scope.Resolve<InMemoryBackgroundJobClient>();
        if (clearFirst) jobClient.Clear();
        jobClient.AutoExecute = value;
    }

    private sealed class Restore : IDisposable
    {
        private readonly WorkSessionForkInheritFlowTests _owner;
        public Restore(WorkSessionForkInheritFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);
    }
}
