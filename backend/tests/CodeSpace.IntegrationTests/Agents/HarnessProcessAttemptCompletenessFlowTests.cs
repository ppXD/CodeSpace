using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.RunData;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The SECOND producer of the run data manifest, against the real capture plane and real Postgres (Rule 12 high
/// fidelity): the <see cref="WorkflowRunDataOwnerKinds.HarnessProcessAttempt"/> facet, produced by the one production
/// site that appends a process attempt row — <see cref="INativeRecordPlane.OpenAsync"/>.
///
/// <para><b>Why this facet and not one whose expectation is discovered.</b> Its expectation is a CONSTANT fixed by a
/// decision rather than a total learned by observing: a launch owes EXACTLY ONE attempt record, and the plane knows
/// that before it writes anything. So the declare-then-write order 0148's header requires is not a batch the producer
/// happened to have in hand — it is a number the producer could state before the fact existed. A resumed opening owes
/// none (<see cref="NativeRecordCaptureRequest.Resume"/> appends no process row), so nothing here counts a re-attach.
/// </para>
///
/// <para><b>What only this tier can execute.</b> Every invariant leant on is a database one — 0137's stale-fence and
/// locator guards are what make a refusal REAL rather than mocked, 0146's fail-closed CHECK is what refuses a complete
/// verdict over a shortfall or an open gap, and its statement-level downgrade trigger plus 0148's rendezvous are what
/// make arrival order not decide the outcome. A unit tier could only assert this producer's constants back at itself.
/// </para>
///
/// <para><b>Nothing READS the manifest in production, and this class adds no reader.</b> With
/// <c>Verdict</c> indeterminate wherever a facet has no producer, a reader today would park every run.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HarnessProcessAttemptCompletenessFlowTests
{
    private const string PricedModel = "claude-sonnet-4-6";

    /// <summary>A well-formed locator: 0137 admits only a JSON OBJECT here, which is what makes <see cref="MalformedLocator"/> a real refusal of a real durable write.</summary>
    private const string Locator = "{\"spoolKey\":\"round-0\"}";

    /// <summary>Valid JSON of the wrong TYPE, so <c>ck_workflow_run_harness_process_attempt_locator</c> refuses the row while the run is still this worker's at its own fence.</summary>
    private const string MalformedLocator = "[]";

    private readonly PostgresFixture _fixture;

    public HarnessProcessAttemptCompletenessFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline: a launch whose process attempt became durable leaves a STATEMENT saying so, in the facet's own
    /// name, with no gap beside it. The expectation is determinate (one launch, one record) — which is the only footing
    /// 0146 permits a complete verdict to rest on.
    /// </summary>
    [Fact]
    public async Task A_launch_whose_process_attempt_landed_states_a_complete_manifest()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunHarnessProcessAttempt.CountAsync(candidate => candidate.Id == handle.AttemptId)).ShouldBe(1,
            customMessage: "the premise: the attempt row is durable, or this test is asserting completeness over nothing");

        var statement = (await db.WorkflowRunDataManifest.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
                    && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessProcessAttempt))
            .ShouldNotBeNull(customMessage: "a launch that recorded its process must leave a completeness statement for the attempt facet — one producer is a case, and a facet nothing produces is indeterminate forever");

        statement.ExpectedRecordCount.ShouldBe(1, customMessage: "a launch owes exactly one attempt record, and it is knowable before the write rather than discovered by it");
        statement.PresentRecordCount.ShouldBe(1);
        statement.KnownMissingCount.ShouldBe(0);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact,
            customMessage: "an attempt row is execution identity, never captured bytes, so there is nothing in it to redact and the verbatim arm is the only complete one reachable");
        statement.Verdict.IsStrictlyReadable().ShouldBeTrue();

        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(0,
            customMessage: "nothing was missing, so nothing may be recorded as missing");
    }

    /// <summary>
    /// A revise round is the next PROCESS of the same execution, so the run owes a second attempt record and the facet
    /// must count both. Counting only the first would leave a complete verdict standing over a process the run has no
    /// record of.
    /// </summary>
    [Fact]
    public async Task A_second_launch_of_the_same_run_is_a_second_record_the_facet_owes()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var first = await OpenAsync(plane, run);
        var second = await OpenAsync(plane, run);

        second.ExecutionId.ShouldBe(first.ExecutionId, customMessage: "the premise: a revise round re-enters the live execution rather than opening a generation");
        second.AttemptId.ShouldNotBe(first.AttemptId);

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(2);
        statement.PresentRecordCount.ShouldBe(2);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    /// <summary>
    /// The divergence this producer can actually suffer while it is still entitled to write: a durable attempt write the
    /// database refuses with the run still this worker's at its own fence. The process it identifies has no record of
    /// its own, so that is a known-missing span a human can locate — and the statement must stop reading complete.
    /// </summary>
    [Fact]
    public async Task A_refused_attempt_this_worker_still_owns_becomes_a_locatable_gap_and_un_completes_the_statement()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        await Should.ThrowAsync<DbUpdateException>(() => OpenRawAsync(plane, run, run.FenceEpoch, MalformedLocator));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var gap = await db.WorkflowRunCaptureGap.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
            && candidate.SubjectKind == WorkflowRunDataOwnerKinds.HarnessProcessAttempt);
        gap.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.HarnessProcessAttempt);
        gap.SubjectId.ShouldNotBeNullOrWhiteSpace(customMessage: "the row this span names is exactly the one that does not exist, and its minted id is the coordinate that locates it");
        gap.RangeKind.ShouldBe(CaptureGapRangeKind.Unbounded,
            customMessage: "one identity record either exists or does not; an ordinal or byte range would invent a coordinate system this span has no position in");
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);
        gap.ReasonDetail.ShouldNotBeNullOrWhiteSpace(customMessage: "a gap with no reason detail is a hole with a label on it");

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1, customMessage: "the expectation was declared before the write, so the shortfall is visible in the counts and not only in the gap");
        statement.PresentRecordCount.ShouldBe(0);
        statement.KnownMissingCount.ShouldBe(1);
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse();
    }

    /// <summary>
    /// The INDETERMINATE arm, and the run this producer must NOT call incomplete either. A refusal after the run was
    /// reclaimed is 0137 doing its job — a superseded worker must not append a process to a run it no longer owns — and
    /// the plane cannot tell from here whether a process ran unrecorded or whether the row was never owed. So the
    /// expectation is UN-STATED rather than left as a shortfall the plane cannot substantiate, and no gap is
    /// manufactured for a span nobody can show is missing.
    ///
    /// <para>It must also STAY unknowable: 0148's advance absorbs into a NULL expectation, so a later launch cannot
    /// count the facet back up to complete.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_attempt_after_the_run_was_reclaimed_unstates_the_expectation_and_records_no_gap()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        await OpenAsync(plane, run);

        using (var reclaimer = _fixture.BeginScope())
            (await reclaimer.Resolve<IAgentRunService>().ReclaimForReattachAsync(run.AgentRunId, CancellationToken.None))
                .ShouldBeTrue(customMessage: "the premise: the run is reclaimed, so the fence this worker holds is stale");

        await Should.ThrowAsync<DbUpdateException>(() => OpenRawAsync(plane, run, run.FenceEpoch, Locator));

        var superseded = await StatementAsync(run);
        superseded.ExpectedRecordCount.ShouldBeNull(
            customMessage: "a refusal the plane cannot attribute to a missing record must not read as a shortfall it can prove — an expectation nobody could establish is what 0146 refuses every complete verdict over");
        superseded.PresentRecordCount.ShouldBe(1, customMessage: "what did land is still a fact, and stays stated");
        superseded.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(0,
                customMessage: "0137 refusing a superseded worker is the intended outcome, not evidence that a record is missing; manufacturing a known-missing span here would be a gap nothing can substantiate");

        await OpenRawAsync(plane, run, run.FenceEpoch + 1, Locator);

        var after = await StatementAsync(run);
        after.ExpectedRecordCount.ShouldBeNull(
            customMessage: "a later launch cannot restore a total nobody ever knew — counting back up to complete here converts the unknown into the assurance 0146 exists to refuse");
        after.PresentRecordCount.ShouldBe(2);
        after.Verdict.IsStrictlyReadable().ShouldBeFalse();
    }

    /// <summary>
    /// WHICH DIRECTION A LOST ACCOUNTING ERRS IN, which is the whole reason the expectation is declared BEFORE the row
    /// rather than counted with it. Both counts are deltas and neither is retryable, so the residue is pointed rather
    /// than removed: the attempt lands, the accounting that follows it is lost, and what remains is a visible shortfall
    /// instead of two counts that fell equally short and read Exact over a record nobody counted.
    /// </summary>
    [Fact]
    public async Task An_attempt_that_landed_with_its_presence_unaccounted_leaves_a_visible_shortfall()
    {
        var run = await SeedWorkflowBoundRunAsync();

        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new PresenceLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());

        var handle = await OpenAsync(planeScope.Resolve<INativeRecordPlane>(), run);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunHarnessProcessAttempt.CountAsync(candidate => candidate.Id == handle.AttemptId)).ShouldBe(1,
            customMessage: "the premise: the attempt row is durable and only the claim about it was lost, or this test asserts nothing about the fail direction");

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1, customMessage: "the declaration stated what the launch undertook, and nothing lowered it");
        statement.PresentRecordCount.ShouldBe(0, customMessage: "the presence advance is the one that was lost");
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a shortfall must not read complete. If this fails, the two counts are advancing together again and a lost accounting reads Exact over a record nobody counted.");
    }

    /// <summary>
    /// THE OTHER DIRECTION A LOST CLAIM CAN GO, and the one a present-only advance turns into a false assurance. When
    /// it is the DECLARATION that is lost rather than the accounting, the producer must not go on to state presence:
    /// 0148's insert reads a present delta over an expected delta of zero as Exact, and its update leaves an
    /// expectation that never counted this launch standing above a presence that did. Either way the facet would read
    /// complete over a record whose obligation nobody established.
    ///
    /// <para>So the launch un-states the expectation instead, exactly as the harness-execution facet already does. The
    /// two arms are the two states the facet can be in when that happens: with nothing stated before, un-stating
    /// invents no row and the absent statement IS the indeterminate answer; with an earlier launch's statement already
    /// there, the expectation it carries is un-stated in place, which the database itself refuses every complete
    /// verdict over.</para>
    /// </summary>
    [Theory]
    [InlineData(0, "absent")]
    [InlineData(1, "LegacyUnknown over expected=null present=1")]
    public async Task A_lost_attempt_declaration_leaves_the_facet_indeterminate_instead_of_counting_a_present_only_delta(int priorLaunches, string indeterminate)
    {
        var run = await SeedWorkflowBoundRunAsync();

        for (var launch = 0; launch < priorLaunches; launch++)
        {
            var accounted = Plane(out var accountedScope);
            using var opened = accountedScope;
            await OpenAsync(accounted, run);
        }

        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new AttemptDeclarationLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());

        var handle = await OpenAsync(planeScope.Resolve<INativeRecordPlane>(), run);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunHarnessProcessAttempt.CountAsync(candidate => candidate.Id == handle.AttemptId)).ShouldBe(1,
            customMessage: "the premise: the attempt row is durable and only the declaration about it was lost, or this test asserts nothing about the fail direction");

        var statement = await StatementOrNullAsync(run);

        Describe(statement).ShouldBe(indeterminate,
            customMessage: "the producer must not follow a lost expectation with a present-only delta. With nothing stated before, that delta writes expected=0 beside present=1 and 0148 reads it as Exact over a launch nobody counted; with an earlier statement standing, it states a presence against an expectation that never counted this launch and reads Exact over a record the facet never undertook.");

        (statement?.Verdict.IsStrictlyReadable() ?? false).ShouldBeFalse();
    }

    /// <summary>The facet's whole answer as one line, so a red run prints what was actually written rather than which of four assertions tripped first.</summary>
    private static string Describe(WorkflowRunDataManifest? statement) =>
        statement is null
            ? "absent"
            : $"{statement.Verdict} over expected={statement.ExpectedRecordCount?.ToString() ?? "null"} present={statement.PresentRecordCount}";

    /// <summary>
    /// A STANDALONE Agent Run belongs to no workflow run and the manifest is keyed to one, so its process attempt is
    /// recorded and NO statement is invented for it — the same named keying gap 0137/0141 already carry. This is the
    /// run whose attempt facet can never honestly be called complete: an absent statement is the indeterminate answer,
    /// which is what a later reader has to treat it as.
    /// </summary>
    [Fact]
    public async Task A_run_that_belongs_to_no_workflow_run_records_its_process_and_states_no_manifest()
    {
        var run = await SeedStandaloneRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);
        handle.WorkflowRunId.ShouldBeNull(customMessage: "the premise: the opening reads its scope off the Agent Run, and this run belongs to no workflow run");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunHarnessProcessAttempt.CountAsync(candidate => candidate.AgentRunId == run.AgentRunId)).ShouldBe(1,
            customMessage: "the capture floor is untouched: the process is recorded whether or not a workflow run exists to key a statement to");
        (await db.WorkflowRunDataManifest.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0,
            customMessage: "the manifest is keyed to a workflow run, so a standalone run states nothing rather than stating it against an invented parent");
        (await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0);
    }

    /// <summary>
    /// ARRIVAL ORDER may not decide the outcome, for the second producer as for the first. A launch's statement and a
    /// gap of another facet are written by two connections at once, many times: the run must never end up with a
    /// strictly readable statement beside an open gap, and the statement must still be THERE — a claim lost to the race
    /// is a delta that is gone, so the run's expectation would be understated for good.
    /// </summary>
    [Fact]
    public async Task A_statement_and_a_gap_racing_each_other_never_leave_a_complete_manifest_beside_an_open_gap()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var run = await SeedWorkflowBoundRunAsync();
            var plane = Plane(out var planeScope);
            using var scope = planeScope;

            await Task.WhenAll(OpenRawAsync(plane, run, run.FenceEpoch, Locator), SeedGapAsync(run));

            using var reader = _fixture.BeginScope();
            var db = reader.Resolve<CodeSpaceDbContext>();

            var open = await db.WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Resolution == CaptureGapResolution.Open);
            var statement = (await db.WorkflowRunDataManifest.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
                        && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessProcessAttempt))
                .ShouldNotBeNull(customMessage: $"attempt {attempt}: the race cost the run its statement entirely — the guard refused a claim computed outside the rendezvous, and the delta it carried is gone");

            open.ShouldBe(1, customMessage: "the gap must be admitted whatever the statement claims — refusing the honest observation to protect the claim is the exact inversion this plane exists to prevent");
            statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
                customMessage: $"attempt {attempt}: the run ended up complete beside an open gap, so the two writers committed blind to each other");
        }
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
                && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessProcessAttempt);
    }

    /// <summary>The facet's statement, or null where the facet has none — because "no row" is itself one of the two indeterminate answers this producer can leave behind.</summary>
    private async Task<WorkflowRunDataManifest?> StatementOrNullAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
                && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessProcessAttempt);
    }

    /// <summary>A gap of ANOTHER facet, so the race is between two writers neither of which can see the other's uncommitted row.</summary>
    private async Task SeedGapAsync(SeededRun run)
    {
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = run.TeamId, WorkflowRunId = run.WorkflowRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.LogSegment, RangeKind = CaptureGapRangeKind.Unbounded,
            Reason = CaptureGapReason.BoundExceeded, ReasonDetail = "a racing producer of another facet",
            CaptureSource = "test-harness/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    private INativeRecordPlane Plane(out ILifetimeScope scope)
    {
        scope = _fixture.BeginScope();

        return scope.Resolve<INativeRecordPlane>();
    }

    private static async Task<NativeRecordCaptureHandle> OpenAsync(INativeRecordPlane plane, SeededRun run) =>
        (await OpenRawAsync(plane, run, run.FenceEpoch, Locator))
            .ShouldNotBeNull(customMessage: "the plane must open against the seeded run, or the test is asserting nothing");

    private static async Task<NativeRecordCaptureHandle?> OpenRawAsync(INativeRecordPlane plane, SeededRun run, long fenceEpoch, string locator) =>
        await plane.OpenAsync(new NativeRecordCaptureRequest
        {
            TeamId = run.TeamId,
            AgentRunId = run.AgentRunId,
            HarnessTypeKey = "claude-code/v2",
            RunnerKind = "local",
            RunnerLocatorJson = locator,
            WorkerFenceEpoch = fenceEpoch,
            Channel = NativeRecordChannel.Stdout,
        }, CancellationToken.None);

    private async Task<SeededRun> SeedWorkflowBoundRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "attempt-completeness-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await CreateAgentRunAsync(teamId, await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));
    }

    private async Task<SeededRun> SeedStandaloneRunAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        return await CreateAgentRunAsync(teamId, workflowRunId: null);
    }

    private async Task<SeededRun> CreateAgentRunAsync(Guid teamId, Guid? workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var created = await runs.CreateAsync(
            new AgentTask { Goal = "record the process it launched", Harness = ClaudeCodeHarness.HarnessKind, Model = PricedModel, TimeoutSeconds = 1800 },
            teamId, workflowRunId, workflowRunId is null ? null : "implement", workflowRunId is null ? "" : "implement#1", CancellationToken.None);

        // The run must be CLAIMED before capture may open against it: the opening carries the claim epoch, and 0137
        // refuses epoch 0 outright.
        return new SeededRun(teamId, created.Id, workflowRunId ?? Guid.Empty, await runs.MarkRunningAsync(created.Id, CancellationToken.None));
    }

    private sealed record SeededRun(Guid TeamId, Guid AgentRunId, Guid WorkflowRunId, long FenceEpoch);

    /// <summary>
    /// The real writer with exactly ONE of the producer's two advances dropped: the one that states presence. That is
    /// the survivable failure the containment already produces in production — a lost claim is reported, not thrown —
    /// so this substitutes the failure rather than a different code path.
    /// </summary>
    /// <summary>
    /// The real writer with the other one of the producer's two advances dropped: the DECLARATION. Same survivable
    /// failure the containment already produces in production — a lost claim is reported as false, never thrown — and
    /// scoped to this facet so the launch's harness-execution statement is untouched.
    /// </summary>
    private sealed class AttemptDeclarationLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public AttemptDeclarationLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Facet == WorkflowRunDataOwnerKinds.HarnessProcessAttempt && advance.Expected > 0
                ? Task.FromResult(false)
                : _real.AdvanceAsync(advance, cancellationToken);

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) => _real.NoticeAsync(gap, cancellationToken);

        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken);
    }

    private sealed class PresenceLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public PresenceLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Present > 0 ? false : await _real.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false);

        public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
            await _real.NoticeAsync(gap, cancellationToken).ConfigureAwait(false);

        public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            await _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false);
    }
}
