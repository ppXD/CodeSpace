using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end proof that <see cref="WorkflowEngine.ExecuteRunAsync"/> refuses to execute a
/// tampered release — i.e. a <c>workflow_version</c> row whose stored
/// <c>definition_hash</c> diverges from <c>DefinitionHash.Compute(definition_json)</c>.
///
/// <para>Why this matters: the publish path (<c>WorkflowService.CreateAsync</c> /
/// <c>UpdateAsync</c>) always writes <c>definition_hash = DefinitionHash.Compute(definition)</c>
/// at INSERT time and stamps <c>committed_at</c>, freezing the row. The
/// <c>workflow_version_enforce_immutability</c> trigger then blocks UPDATE/DELETE on the
/// committed row at the DB layer. The only way the two columns can disagree is if someone
/// bypassed BOTH the publish path AND the trigger — a malicious operator forging the JSON
/// directly. <see cref="WorkflowEngine"/>'s pre-execution hash check
/// (<c>LoadDefinitionAndHashAsync</c>) catches this at runtime and short-circuits the run
/// into Failure via <c>MarkBootstrapFailureAsync</c>, AND emits a <c>run.failed</c> ledger
/// record so the timeline UI surfaces the cause.</para>
///
/// <para><b>Staging strategy (Option A — "born tampered")</b>: the immutability trigger
/// fires BEFORE UPDATE/DELETE only — INSERT of a fresh row is allowed. So we directly
/// INSERT a NEW <c>workflow_version</c> row (version=2 on the same workflow) supplying a
/// <c>definition_hash</c> that deliberately does NOT match
/// <c>DefinitionHash.Compute(definition_jsonb)</c>. The row is "born tampered" and the
/// trigger never gets a chance to refuse — exercising the engine's runtime check without
/// disabling the trigger (which would race with parallel tests).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReleaseTamperDetectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public ReleaseTamperDetectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>All-zeros 64-hex SHA-256 — guaranteed to differ from any real definition hash.</summary>
    private const string TamperedHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task Engine_marks_run_Failure_with_diagnostic_error_and_emits_run_failed_ledger_when_stored_hash_diverges_from_recomputed_hash()
    {
        // ── Arrange ────────────────────────────────────────────────────────────
        // 1. Create a workflow normally — produces a clean workflow_version v1 with matched
        //    hash. We never touch v1; it's just satisfying the workflow row's FK chain so
        //    the INSERT below has a parent workflow_id to reference.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = WorkflowsTestSeed.MinimalDefinition();
        var workflowId = await CreateWorkflowAsync(teamId, userId, def);

        // 2. INSERT a NEW workflow_version row at version=2 whose definition_hash column is
        //    all-zeros — guaranteed not to equal DefinitionHash.Compute(def). The
        //    definition_jsonb is a perfectly valid serialised definition, so the engine's
        //    JSON.Deserialize step succeeds and we reach the hash check.
        const int tamperedVersion = 2;
        var tamperedDefinitionJson = JsonSerializer.Serialize(def, WorkflowJson.Options);
        var expectedRecomputedHash = DefinitionHash.Compute(def);

        await InsertTamperedVersionAsync(workflowId, tamperedVersion, tamperedDefinitionJson, TamperedHash, userId);

        // 3. Stage a WorkflowRun pointing at v2 directly in Enqueued state — production
        //    arrives here via the dispatcher's Pending→Enqueued CAS, but the test seeds the
        //    Enqueued state straight away because we're exercising the engine's claim path,
        //    not the dispatcher's.
        var runId = await StageEnqueuedRunAsync(workflowId, teamId, tamperedVersion);

        // ── Act ────────────────────────────────────────────────────────────────
        // Drive the real engine. The Enqueued→Running CAS succeeds (one writer), then
        // RunAfterClaimAsync → LoadDefinitionAndHashAsync throws ReleaseTamperedException,
        // which propagates to ExecuteRunAsync's catch(Exception) → MarkBootstrapFailureAsync.
        // No exception bubbles out of ExecuteRunAsync; the failure is captured ONTO the row.
        using (var engineScope = _fixture.BeginScope())
        {
            await engineScope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
        }

        // ── Assert ─────────────────────────────────────────────────────────────
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        // (1) Terminal status — must be Failure, NOT Running (stuck) and NOT Success
        //     (tampered release executed). Anything else means the bootstrap guard is broken.
        run.Status.ShouldBe(WorkflowRunStatus.Failure,
            customMessage: "engine MUST mark the run Failure when stored definition_hash diverges from the recomputed hash. " +
                           "Status=Running would mean the bootstrap-failure CAS didn't fire (the row leaks). " +
                           "Status=Success would mean a tampered release executed end-to-end (side-effecting nodes may have fired). " +
                           "Either is a critical regression of the no-tampered-execution invariant.");

        run.CompletedAt.ShouldNotBeNull(
            "MarkBootstrapFailureAsync sets completed_at alongside status — without it the run looks 'still running' to monitoring");

        // (2) Error column carries the canonical bootstrap-failure marker AND the diagnostic
        //     fields an operator needs to triage WHICH workflow, WHICH version, WHAT hashes.
        //     This is the operator's primary signal: it appears in the run-detail UI's
        //     status banner.
        run.Error.ShouldNotBeNull("MarkBootstrapFailureAsync MUST populate Error so the operator sees the cause");
        run.Error!.ShouldContain("Engine bootstrap failure (ReleaseTamperedException)",
            customMessage: "MarkBootstrapFailureAsync's prefix is the operator's single source of truth for distinguishing " +
                           "bootstrap failures (pre-walker — config / hash / scope) from node failures (runtime — node threw). " +
                           "The exception type name MUST be in the message verbatim");
        run.Error.ShouldContain(workflowId.ToString(),
            customMessage: "operator needs the workflow id to locate the tampered version row in the DB");
        run.Error.ShouldContain($"version {tamperedVersion}",
            customMessage: "operator needs the version number to know WHICH workflow_version row drifted (a workflow can have many versions)");
        run.Error.ShouldContain(TamperedHash,
            customMessage: "operator needs the stored (expected) hash to know what the publish path originally committed");
        run.Error.ShouldContain(expectedRecomputedHash,
            customMessage: "operator needs the recomputed hash to know what the current JSON actually hashes to — " +
                           "the diff of stored vs recomputed is the fingerprint of the tampering");

        // (3) Ledger gets a run.failed record. The timeline UI reads workflow_run_record (not
        //     workflow_run.Error directly) for the chronological story of the run; a missing
        //     ledger record means the timeline pane silently drops the failure.
        var failedRecord = await db.WorkflowRunRecord.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunFailed);
        failedRecord.ShouldNotBeNull(
            "run.failed ledger record is what the timeline UI reads to show the operator WHY the run failed. " +
            "Missing this record means the failure is invisible in the run-detail timeline — only the status banner shows it");
        failedRecord!.PayloadJson.ShouldContain("Engine bootstrap failure (ReleaseTamperedException)",
            customMessage: "ledger payload's error field MUST mirror workflow_run.Error so the timeline + status banner tell the same story. " +
                           "Drift between the two is a sign that one write path didn't run");

        // (4) Walker never started — no run.completed (would mean walker reached terminal),
        //     no run.cancelled (a different terminal path), no node.* records (walker
        //     emitting node lifecycle events). The bootstrap-failure path MUST short-circuit
        //     BEFORE the walker, otherwise side-effecting nodes could run against tampered
        //     config / scope.
        var post = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => r.RecordType)
            .ToListAsync();
        post.ShouldNotContain(WorkflowRunRecordTypes.RunCompleted,
            customMessage: "bootstrap failure MUST short-circuit before the walker — emitting run.completed alongside run.failed " +
                           "would corrupt the lifecycle and let downstream consumers double-count the run as both success AND failure");
        post.ShouldNotContain(WorkflowRunRecordTypes.NodeStarted,
            customMessage: "no node may begin execution under a tampered release — node.started after a hash mismatch means " +
                           "a side-effecting node (post_pr_comment, http.request) just fired against unverified config");
        post.ShouldNotContain(WorkflowRunRecordTypes.NodeCompleted,
            customMessage: "no node may complete execution under a tampered release — see node.started rationale");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "tamper-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>
    /// Direct INSERT of a workflow_version row whose stored <c>definition_hash</c>
    /// deliberately diverges from <c>DefinitionHash.Compute(definition_jsonb)</c>. The
    /// immutability trigger fires on UPDATE/DELETE only — INSERT of a fresh row is allowed,
    /// so this "born tampered" approach exercises the engine's runtime hash check without
    /// disabling the trigger (which would race with parallel tests sharing the same PG
    /// cluster).
    /// </summary>
    private async Task InsertTamperedVersionAsync(Guid workflowId, int version, string definitionJson, string tamperedHash, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        // ExecuteSqlInterpolatedAsync parameterises every interpolated value — the JSON
        // string is bound as a text parameter and then ::jsonb cast applies to the parameter
        // value (see WorkflowVersionImmutabilityTests for the same pattern). This is safe
        // against quote escaping in the serialised definition.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO workflow_version (workflow_id, version, definition_jsonb, definition_hash, committed_at, created_date, created_by)
               VALUES ({workflowId}, {version}, {definitionJson}::jsonb, {tamperedHash}, {now}, {now}, {actorUserId})");
    }

    /// <summary>
    /// Stages a WorkflowRunRequest + WorkflowRun pair pointing at the tampered version,
    /// directly in <c>Enqueued</c> status (mirrors <see cref="WorkflowsTestSeed.SeedManualRunAsync"/>
    /// but pinned to a caller-supplied <c>workflowVersion</c> so we can target v2). We seed
    /// straight to Enqueued because the engine's entry CAS expects Enqueued→Running — we're
    /// exercising the engine, not the dispatcher.
    /// </summary>
    private async Task<Guid> StageEnqueuedRunAsync(Guid workflowId, Guid teamId, int workflowVersion)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = workflowId,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowVersion = workflowVersion,
            TeamId = teamId,
            RunRequestId = requestId,
            Status = WorkflowRunStatus.Enqueued,
            EnqueuedAt = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }
}
