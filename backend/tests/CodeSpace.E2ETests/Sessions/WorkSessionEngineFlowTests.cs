using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Sessions;

/// <summary>
/// The WorkSession layer (S1) against the ASSEMBLED engine — proves the additive migration 0069 is non-breaking on
/// the headline run flow AND that the session binding survives a FULL real-engine walk (the executor's status /
/// timestamp / outputs writes do not clobber <c>session_id</c> / <c>session_turn_index</c>):
///   (a) a snapshot run with NO session walks start → terminal Success and lands with both columns NULL —
///       byte-identical to pre-session behaviour, proven through the whole engine, not just the starter;
///   (b) a snapshot run staged WITH a pre-resolved <see cref="SessionAssignment"/> walks to terminal Success and
///       STILL carries its SessionId + turn ordinal afterwards.
///
/// <para>Tier: high-fidelity E2E (Surface=Engine) — real <see cref="IRunFromSnapshotStarter"/> + real
/// <see cref="IWorkflowEngine"/> over real Postgres, no mocks.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class WorkSessionEngineFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionEngineFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Session_less_run_walks_to_terminal_with_null_session_columns()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = await StartSnapshotAsync(teamId, userId, session: null);
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the headline snapshot-run flow must still reach terminal Success with the new schema");
        run.SessionId.ShouldBeNull("the additive session columns default NULL end-to-end through the engine");
        run.SessionTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task Session_bound_run_keeps_its_binding_through_the_engine_walk()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        var runId = await StartSnapshotAsync(teamId, userId,
            session: new SessionAssignment { SessionId = sessionId, TurnIndex = 1 });
        await RunEngineAsync(runId);

        var run = await LoadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.SessionId.ShouldBe(sessionId,
            customMessage: "the engine's status/output writes MUST NOT clobber the run's session binding");
        run.SessionTurnIndex.ShouldBe(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> StartSnapshotAsync(Guid teamId, Guid userId, SessionAssignment? session)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
            EchoDefinition(), teamId, userId, launchPayloadJson: "{}", scopeRepositoryIds: null,
            projectionKind: null, session, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession
        {
            Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private static WorkflowDefinition EchoDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };
}
