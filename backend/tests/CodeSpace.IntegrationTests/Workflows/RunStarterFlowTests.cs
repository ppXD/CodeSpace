using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the contract of <see cref="IRunStarter"/>:
///   1. Manual envelope → request(SourceType=manual, ActorType=user, ActorId set) + run(Pending) + run.queued ledger
///   2. Webhook envelope → request(matcher TypeKey, ActorType=webhook, ActorId=null, activation lineage) + run(Pending)
///   3. Replay envelope → request(SourceType=replay, CausationId set) + run(Pending, ParentRunId, ReleaseHashAtRun)
///   4. Validation: ActorType=User without ActorId throws; replay partials throw
///
/// The starter inserts no outbox row — workflow_run.Status itself IS the queue (PostBoy pattern):
/// the caller (WorkflowService / RunSourceDispatcher) hands the runId to
/// <see cref="Dispatch.IWorkflowRunDispatcher"/>, which atomically CAS Pending→Enqueued then hands
/// it to the background-job client. The no-double-execution guarantee is proved by
/// <see cref="NoDoubleExecutionFlowTests"/>.
///
/// The integration tier covers the real DB writes; unit tier covers the pure-validation
/// branches (in WorkflowRunActorTypesTests for the constant pinning).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunStarterFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunStarterFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Manual_envelope_creates_request_run_outbox_and_run_queued()
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
                NormalizedPayloadJson = """{"trigger":"manual"}""",
                CreatedBy = userId,
            }, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var run = await verifyDb.WorkflowRun.AsNoTracking().Include(r => r.RunRequest).SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Pending);
        run.TeamId.ShouldBe(teamId);
        run.RunRequest.SourceType.ShouldBe(WorkflowRunSourceTypes.Manual);
        run.RunRequest.ActorType.ShouldBe(WorkflowRunActorTypes.User);
        run.RunRequest.ActorId.ShouldBe(userId);
        run.RunRequest.Status.ShouldBe(WorkflowRunRequestStatus.Consumed);

        // The RunWorkflow outbox discriminator + handler + payload do not exist in the
        // codebase — no string/handler can produce such a row, enforced at compile time.
        // The no-double-execution guarantee is proved by NoDoubleExecutionFlowTests via
        // direct observation of "exactly one engine execution per runId" rather than
        // indirectly via outbox-row presence.

        // run.queued ledger record emitted with the canonical source type.
        var queued = await verifyDb.WorkflowRunRecord.AsNoTracking()
            .SingleAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunQueued);
        queued.NodeId.ShouldBeNull();
    }

    [Fact]
    public async Task Webhook_envelope_sets_activation_lineage_and_null_actor_id()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        // workflow_run_request.activation_id has an FK to workflow_activation; seed a real
        // activation row so the test exercises the production lineage rather than a fabricated id.
        Guid activationId;
        using (var setup = _fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            activationId = Guid.NewGuid();
            db.WorkflowActivation.Add(new CodeSpace.Core.Persistence.Entities.WorkflowActivation
            {
                Id = activationId,
                WorkflowId = workflowId,
                TypeKey = "trigger.pr.opened",
                ConfigJson = "{}",
                Enabled = true,
                CreatedBy = SystemUsers.SeederId,
                LastModifiedBy = SystemUsers.SeederId,
            });
            await db.SaveChangesAsync();
        }

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
                SourceType = "trigger.pr.opened",
                ActorType = WorkflowRunActorTypes.Webhook,
                ActorId = null,
                NormalizedPayloadJson = """{"number":42}""",
                CreatedBy = SystemUsers.SeederId,
                ActivationId = activationId,
                ActivationSnapshotJson = """{"id":"...","typeKey":"trigger.pr.opened"}""",
            }, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var request = await verifyDb.WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.WorkflowId == workflowId && r.SourceType == "trigger.pr.opened");
        request.ActorType.ShouldBe(WorkflowRunActorTypes.Webhook);
        request.ActorId.ShouldBeNull("webhook actors are anonymous — ActorId MUST be null per envelope validation");
        request.ActivationId.ShouldBe(activationId);
        request.ActivationSnapshotJson.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Replay_envelope_sets_causation_and_parent_run_lineage()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        // First, an "original" run we'll replay from.
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        Guid originalRequestId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            originalRequestId = await db.WorkflowRun.AsNoTracking().Where(r => r.Id == originalRunId).Select(r => r.RunRequestId).SingleAsync();
        }

        Guid replayRunId;
        using (var scope = _fixture.BeginScope())
        {
            var starter = scope.Resolve<IRunStarter>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            replayRunId = await starter.StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.Replay,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = userId,
                NormalizedPayloadJson = "{}",
                CreatedBy = userId,
                CausationRequestId = originalRequestId,
                ParentRunId = originalRunId,
                ReleaseHashAtRun = "abc123",
            }, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();

        var replayRun = await verifyDb.WorkflowRun.AsNoTracking().Include(r => r.RunRequest).SingleAsync(r => r.Id == replayRunId);
        replayRun.ParentRunId.ShouldBe(originalRunId,
            "replay run.ParentRunId MUST point back at the original — engine reads this for the replay path");
        replayRun.ReleaseHashAtRun.ShouldBe("abc123",
            "replay run.ReleaseHashAtRun MUST be copied from original so the engine's hash-verification doesn't fail");
        replayRun.RunRequest.CausationId.ShouldBe(originalRequestId,
            "replay request.CausationId MUST point back at the original request — audit lineage");
        replayRun.RunRequest.SourceType.ShouldBe(WorkflowRunSourceTypes.Replay);
    }

    [Fact]
    public async Task User_actor_without_id_throws()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var starter = scope.Resolve<IRunStarter>();

        await Should.ThrowAsync<ArgumentException>(() => starter.StartAsync(new RunSourceEnvelope
        {
            TeamId = teamId,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = WorkflowRunActorTypes.User,
            ActorId = null,                                  // violation
            NormalizedPayloadJson = "{}",
            CreatedBy = SystemUsers.SeederId,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Webhook_actor_with_id_throws()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var starter = scope.Resolve<IRunStarter>();

        await Should.ThrowAsync<ArgumentException>(() => starter.StartAsync(new RunSourceEnvelope
        {
            TeamId = teamId,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            SourceType = "trigger.pr.opened",
            ActorType = WorkflowRunActorTypes.Webhook,
            ActorId = userId,                                // violation
            NormalizedPayloadJson = "{}",
            CreatedBy = SystemUsers.SeederId,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Replay_envelope_with_partial_lineage_throws()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var starter = scope.Resolve<IRunStarter>();

        await Should.ThrowAsync<ArgumentException>(() => starter.StartAsync(new RunSourceEnvelope
        {
            TeamId = teamId,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            SourceType = WorkflowRunSourceTypes.Replay,
            ActorType = WorkflowRunActorTypes.User,
            ActorId = userId,
            NormalizedPayloadJson = "{}",
            CreatedBy = userId,
            ParentRunId = Guid.NewGuid(),                    // set
            CausationRequestId = null,                        // missing — violation
            ReleaseHashAtRun = null,                          // missing
        }, CancellationToken.None));
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "starter-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
