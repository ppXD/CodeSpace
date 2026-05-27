using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the engine's "no-double-execution" guarantee via atomic CAS, and the downstream
/// responsibility split for stale-run recovery.
///
/// <para>Scenario: a worker started a run (CAS Enqueued → Running succeeded), executed some
/// nodes (possibly including side-effecting ones like POST a PR comment), then crashed. The
/// Hangfire job's lease expired, the recurring reconciler observed the row stuck in Running
/// past its expected duration, and re-enqueued the run. A new worker calls the engine's
/// ExecuteRunAsync on the same run_id. At this point the row is in <c>Running</c>.</para>
///
/// <para>The engine's contract:</para>
///
/// <list type="bullet">
///   <item>The entry CAS WHERE clause requires <c>Status == Enqueued</c>. Any other state
///   (Running, Success, Failure, Cancelled, Pending) yields rows-affected = 0.</item>
///   <item>On rows-affected = 0 the engine returns silently — no mutation, no ledger emit,
///   no exception. This is the no-double-execution guarantee: two workers cannot both
///   transition the same row Enqueued → Running.</item>
///   <item>Marking a stuck Running row Failure is the <c>StuckRunReconcilerRecurringJob</c>'s
///   responsibility, NOT the engine's. The engine cannot tell from a Running row whether
///   "another worker is actively executing this right now" vs "a previous worker crashed";
///   only the reconciler, which has the duration heuristic, can make that call safely.</item>
/// </list>
///
/// <para>Why the engine MUST NOT mark Failure inline: if the row is Running because another
/// worker is actively executing nodes (e.g. mid-LLM-call), an inline "mark Failure" would
/// corrupt the run's status mid-execution — the original worker would land its node.completed
/// records against a row that's already Failure. The silent short-circuit defers that
/// decision to the reconciler, which checks "is this row Running but with no ledger activity
/// for N minutes?" and acts only when truly abandoned.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AbandonedRunRecoveryFlowTests
{
    private readonly PostgresFixture _fixture;

    public AbandonedRunRecoveryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Engine_on_running_run_short_circuits_silently_no_double_execution()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Simulate the previous worker successfully claimed + is mid-execution: row is Running.
        // (Whether the original worker actually crashed or is still alive is unknowable from
        // the engine's vantage point — that's precisely why the engine MUST defer.)
        using (var setup = _fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.Status = WorkflowRunStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        // Capture the pre-state so we can prove the engine was a true no-op on this re-entry.
        int preLedgerCount;
        using (var snapshot = _fixture.BeginScope())
        {
            preLedgerCount = await snapshot.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
                .CountAsync(r => r.RunId == runId);
        }

        // Re-invoke the engine — simulates a second worker picking up a Hangfire retry or the
        // reconciler's re-enqueue. The CAS Enqueued → Running fails (row is Running, not
        // Enqueued); engine returns silently.
        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        var finalRun = await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        finalRun.Status.ShouldBe(WorkflowRunStatus.Running,
            "engine MUST leave a Running row untouched on re-entry — flipping it to Failure inline " +
            "would corrupt the row mid-execution if another worker is genuinely still alive. " +
            "Marking abandoned runs Failure is the reconciler's job (duration-based heuristic), not the engine's");
        finalRun.Error.ShouldBeNull("engine MUST NOT write an Error on silent short-circuit — the abandoned-run determination is the reconciler's");

        var postLedgerCount = await verifyDb.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
        postLedgerCount.ShouldBe(preLedgerCount,
            "engine MUST emit NO ledger records on silent short-circuit — re-entry has no side effects on the run row OR the ledger");

        var failedRecord = await verifyDb.WorkflowRunRecord.AsNoTracking()
            .AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed);
        failedRecord.ShouldBeFalse("engine MUST NOT emit run.failed for a Running row — that's the reconciler's decision");
    }

    [Fact]
    public async Task Engine_on_terminal_run_short_circuits_without_writing_failure()
    {
        // Already-terminal runs (Success / Failure / Cancelled) MUST be left untouched. Pin
        // this so the Running guard doesn't bleed into the terminal cases.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var setup = _fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.Status = WorkflowRunStatus.Success;
            run.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            run.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-9);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            // Engine sees terminal status and returns immediately — no exception, no
            // mutation, no ledger records.
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        var finalRun = await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        finalRun.Status.ShouldBe(WorkflowRunStatus.Success,
            "terminal runs MUST stay at their terminal status — engine re-entry on a Success run is a no-op");

        // No spurious run.failed for an already-Success run.
        var failedExists = await verifyDb.WorkflowRunRecord.AsNoTracking()
            .AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed);
        failedExists.ShouldBeFalse("engine MUST NOT emit run.failed when short-circuiting an already-terminal run");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, CodeSpace.Messages.Dtos.Workflows.WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "abandoned-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
