using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The WorkSession layer (S1) schema, proven against real Postgres:
///   (a) <c>work_session</c> round-trips through EF — Kind/Status persist as the enum NAME (HasConversion),
///       the jsonb scope + summary survive, audit fields auto-populate;
///   (b) the DEFAULT is byte-identical — a run staged through the REAL starters with NO session leaves
///       <c>session_id</c> / <c>session_turn_index</c> NULL (the zero-behaviour-change guarantee);
///   (c) a pre-resolved <see cref="SessionAssignment"/> binds the run to its session + turn at BOTH creation
///       sites (snapshot <see cref="IRunFromSnapshotStarter"/> and authored <see cref="IRunStarter"/>);
///   (d) an INHERITED turn (assignment with a null TurnIndex) carries the SessionId yet no new turn ordinal —
///       the child/replay shape from Correction 1.
///
/// <para>Tier: high-fidelity Integration — the REAL production starters write the rows; real Postgres holds the
/// schema migration 0069 produced. No mocks.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionSchemaFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionSchemaFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task WorkSession_round_trips_with_kind_status_jsonb_summary_and_audit()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var sessionId = Guid.CreateVersion7();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkSession.Add(new WorkSession
            {
                Id = sessionId,
                TeamId = teamId,
                Title = "Fix the retry backoff",
                Kind = WorkSessionKind.Pr,
                Status = WorkSessionStatus.Open,
                ScopeJson = """{"repos":["backend"]}""",
                Summary = "turn 1 produced a fix; awaiting review",
            });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        var session = await vdb.WorkSession.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        session.TeamId.ShouldBe(teamId);
        session.Title.ShouldBe("Fix the retry backoff");
        session.Kind.ShouldBe(WorkSessionKind.Pr);
        session.Status.ShouldBe(WorkSessionStatus.Open);
        session.Summary.ShouldBe("turn 1 produced a fix; awaiting review");

        // scope_jsonb is jsonb — Postgres canonicalises it (re-spaced, key-ordered) rather than preserving the
        // input bytes, so assert the parsed CONTENT, not the literal string.
        session.ScopeJson.ShouldNotBeNull();
        using var scopeDoc = System.Text.Json.JsonDocument.Parse(session.ScopeJson);
        scopeDoc.RootElement.GetProperty("repos")[0].GetString().ShouldBe("backend",
            "the jsonb scope map MUST round-trip its data through real Postgres");
        session.CreatedDate.ShouldNotBe(default, "IAuditable audit hook MUST stamp created_date");
        session.CreatedBy.ShouldNotBe(Guid.Empty, "IAuditable audit hook MUST stamp created_by");

        // Kind is stored as the enum NAME, not its ordinal — the HasConversion<string> mapping the wire-value
        // unit test pins. Filter on the raw literal 'Pr' to prove the physical storage: were HasConversion missing,
        // the column would hold the ordinal and this match returns nothing.
        var byRawKind = await vdb.WorkSession
            .FromSqlInterpolated($"SELECT * FROM work_session WHERE id = {sessionId} AND kind = 'Pr'")
            .AsNoTracking()
            .ToListAsync();
        byRawKind.Count.ShouldBe(1, "kind MUST persist as the enum name 'Pr' so a rename is a test-visible break");
    }

    [Fact]
    public async Task Snapshot_starter_with_null_session_leaves_run_unbound()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                MinimalSnapshot(), teamId, userId, launchPayloadJson: "{}", scopeRepositoryIds: null,
                projectionKind: null, session: null, CancellationToken.None);
        }

        var run = await LoadRunAsync(runId);
        run.SessionId.ShouldBeNull("a null SessionAssignment is byte-identical to pre-session behaviour");
        run.SessionTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task Snapshot_starter_with_assignment_binds_run_to_session_and_turn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                MinimalSnapshot(), teamId, userId, launchPayloadJson: "{}", scopeRepositoryIds: null,
                projectionKind: null,
                session: new SessionAssignment { SessionId = sessionId, TurnIndex = 3 },
                CancellationToken.None);
        }

        var run = await LoadRunAsync(runId);
        run.SessionId.ShouldBe(sessionId, "the pre-resolved SessionAssignment binds the run to its thread");
        run.SessionTurnIndex.ShouldBe(3, "a top-level turn carries its ordinal");
    }

    [Fact]
    public async Task Snapshot_starter_inherited_turn_binds_session_with_null_turn_index()
    {
        // Correction 1: a child / replay INHERITS the session yet consumes NO new turn — TurnIndex null.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                MinimalSnapshot(), teamId, userId, launchPayloadJson: "{}", scopeRepositoryIds: null,
                projectionKind: null,
                session: new SessionAssignment { SessionId = sessionId, TurnIndex = null },
                CancellationToken.None);
        }

        var run = await LoadRunAsync(runId);
        run.SessionId.ShouldBe(sessionId, "an inherited run still rides its parent's session");
        run.SessionTurnIndex.ShouldBeNull("an inherited child/replay turn consumes no new ordinal");
    }

    [Fact]
    public async Task Manual_starter_with_assignment_binds_run_to_session()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var sessionId = await SeedSessionAsync(teamId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var starter = scope.Resolve<IRunStarter>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            runId = await starter.StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.Manual,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = userId,
                NormalizedPayloadJson = "{}",
                CreatedBy = userId,
                Session = new SessionAssignment { SessionId = sessionId, TurnIndex = 1 },
            }, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        var run = await LoadRunAsync(runId);
        run.SessionId.ShouldBe(sessionId, "the authored creation site writes the same pre-resolved binding");
        run.SessionTurnIndex.ShouldBe(1);
    }

    [Fact]
    public async Task Manual_starter_without_session_leaves_run_unbound()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var starter = scope.Resolve<IRunStarter>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            runId = await starter.StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.Manual,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = userId,
                NormalizedPayloadJson = "{}",
                CreatedBy = userId,
            }, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        var run = await LoadRunAsync(runId);
        run.SessionId.ShouldBeNull("an envelope without a Session is byte-identical to pre-session behaviour");
        run.SessionTurnIndex.ShouldBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

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

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "session-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static WorkflowDefinition MinimalSnapshot() => new()
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
