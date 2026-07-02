using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="IWorkflowService"/> + resume CAS + record ledger from DI): the
/// operator wait-reissue verb end-to-end. A STRANDED signal-driven wait — a Timer whose scheduled wake was dropped, a
/// Callback whose external system never posts — is force-resolved through the same idempotent resolve-first CAS every
/// real signal uses: the wait flips Resolved, the run un-strands (Suspended → Pending → Enqueued), and an audited
/// <c>wait.reissued</c> record lands. A decision-driven wait is refused; an already-resolved wait no-ops; a foreign
/// team's run is a 404.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WaitReissueFlowTests
{
    private readonly PostgresFixture _fixture;

    public WaitReissueFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Reissuing_a_stranded_timer_fires_the_wake_un_strands_the_run_and_audits_the_override()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (runId, waitId) = await SeedSuspendedRunWithWaitAsync(teamId, WorkflowWaitKinds.Timer, wakeAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var outcome = await ReissueAsync(runId, waitId, teamId, userId, payloadJson: null);

        outcome.ShouldBe(ReissueWaitOutcome.Reissued);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunWait.AsNoTracking().Where(w => w.Id == waitId).Select(w => w.Status).SingleAsync())
            .ShouldBe(WorkflowWaitStatuses.Resolved, "the dropped timer wake was fired now");
        (await db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync())
            .ShouldBe(WorkflowRunStatus.Enqueued, "the run un-stranded via Suspended → Pending → the post-commit dispatch's Pending → Enqueued");
        (await db.WorkflowRunRecord.AsNoTracking().AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.WaitReissued))
            .ShouldBeTrue("the operator override is audited on the ledger");
    }

    [Fact]
    public async Task Reissuing_a_dead_callback_resolves_it_with_the_operator_body()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (runId, waitId) = await SeedSuspendedRunWithWaitAsync(teamId, WorkflowWaitKinds.Callback, wakeAt: null);

        var outcome = await ReissueAsync(runId, waitId, teamId, userId, payloadJson: """{"decision":"proceed"}""");

        outcome.ShouldBe(ReissueWaitOutcome.Reissued);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var payload = await db.WorkflowRunWait.AsNoTracking().Where(w => w.Id == waitId).Select(w => w.PayloadJson).SingleAsync();
        payload.ShouldNotBeNull();
        JsonDocument.Parse(payload!).RootElement.GetProperty("decision").GetString()
            .ShouldBe("proceed", "the operator-supplied body is stamped as the callback's resume payload (surfaced as the node's body output)");
    }

    [Fact]
    public async Task Reissuing_a_human_decision_wait_is_refused_as_unsupported_and_leaves_it_pending()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (runId, waitId) = await SeedSuspendedRunWithWaitAsync(teamId, WorkflowWaitKinds.Approval, wakeAt: null);

        var outcome = await ReissueAsync(runId, waitId, teamId, userId, payloadJson: null);

        outcome.ShouldBe(ReissueWaitOutcome.UnsupportedKind, "an Approval carries a human decision — it resolves via /resume, never a blind reissue");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunWait.AsNoTracking().Where(w => w.Id == waitId).Select(w => w.Status).SingleAsync())
            .ShouldBe(WorkflowWaitStatuses.Pending, "the refused wait is untouched — still parked for its own verb");
    }

    [Fact]
    public async Task Reissuing_an_already_resolved_wait_is_an_idempotent_no_op()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (runId, waitId) = await SeedSuspendedRunWithWaitAsync(teamId, WorkflowWaitKinds.Timer, wakeAt: DateTimeOffset.UtcNow.AddMinutes(-1), status: WorkflowWaitStatuses.Resolved);

        (await ReissueAsync(runId, waitId, teamId, userId, payloadJson: null))
            .ShouldBe(ReissueWaitOutcome.AlreadyResolved, "a wait a real signal / deadline already resolved is a no-op — the reissue verb is idempotent");
    }

    [Fact]
    public async Task Reissuing_a_wait_on_a_foreign_teams_run_is_a_404()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, otherUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (runId, waitId) = await SeedSuspendedRunWithWaitAsync(teamId, WorkflowWaitKinds.Timer, wakeAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await Should.ThrowAsync<KeyNotFoundException>(() => ReissueAsync(runId, waitId, otherTeamId, otherUserId, payloadJson: null));
    }

    private async Task<ReissueWaitOutcome> ReissueAsync(Guid runId, Guid waitId, Guid teamId, Guid userId, string? payloadJson)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ReissueWaitAsync(runId, waitId, teamId, userId, payloadJson, CancellationToken.None);
    }

    private async Task<(Guid RunId, Guid WaitId)> SeedSuspendedRunWithWaitAsync(Guid teamId, string waitKind, DateTimeOffset? wakeAt, string status = WorkflowWaitStatuses.Pending)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var waitId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Suspended,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        // Persist the run BEFORE the wait: EF doesn't model the wait → run relationship (RunId is a plain column with a
        // DB-only FK), so a single SaveChanges can insert the wait first and trip the FK.
        await db.SaveChangesAsync();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId, RunId = runId, NodeId = "delay", IterationKey = string.Empty, WaitKind = waitKind,
            Token = Guid.NewGuid().ToString("N"), WakeAt = wakeAt, Status = status,
            PayloadJson = status == WorkflowWaitStatuses.Resolved ? "{}" : null,
            CreatedAt = now, ResolvedAt = status == WorkflowWaitStatuses.Resolved ? now : null,
        });

        await db.SaveChangesAsync();
        return (runId, waitId);
    }
}
