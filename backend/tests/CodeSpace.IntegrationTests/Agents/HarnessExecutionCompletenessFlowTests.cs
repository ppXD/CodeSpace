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
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>The harness-execution facet's producer contract against its real writer and real PostgreSQL guards.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HarnessExecutionCompletenessFlowTests
{
    private const string Locator = "{\"spoolKey\":\"round-0\"}";
    private const string MalformedLocator = "[]";
    private const string PricedModel = "claude-sonnet-4-6";
    private readonly PostgresFixture _fixture;

    public HarnessExecutionCompletenessFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_new_execution_declares_one_expected_record_before_it_lands_and_accounts_for_it_after()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var advances = new List<RunDataFacetAdvance>();
        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new RecordingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance), advances))
            .InstancePerLifetimeScope());

        var handle = await OpenAsync(planeScope.Resolve<INativeRecordPlane>(), run);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.Id == handle.ExecutionId)).ShouldBe(1,
            customMessage: "the stable execution identity the producer promised is durable");

        advances.Where(advance => advance.Facet == WorkflowRunDataOwnerKinds.HarnessExecution)
            .Select(advance => (advance.Expected, advance.Present, advance.Masked))
            .ShouldBe(new[] { (1L, 0L, false), (0L, 1L, false) },
                customMessage: "the exact K=1 expectation must precede the execution row and its presence must follow it in a separate contained write");

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(1);
        statement.KnownMissingCount.ShouldBe(0);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    [Fact]
    public async Task A_second_process_in_the_same_live_execution_does_not_count_a_second_execution()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var first = await OpenAsync(plane, run);
        var second = await OpenAsync(plane, run);

        second.ExecutionId.ShouldBe(first.ExecutionId, customMessage: "the premise: a revise round appends a process to the live execution");
        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(1);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    [Fact]
    public async Task A_later_generation_is_a_second_execution_record_the_run_owes()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var first = await OpenAsync(plane, run);
        await plane.CloseAsync(first, exitCode: 0, CancellationToken.None);
        await ((INativeRecordExecutionPlane)plane).TerminalizeAsync(run.TeamId, run.AgentRunId, run.FenceEpoch, CancellationToken.None);
        var second = await OpenAsync(plane, run);

        second.ExecutionId.ShouldNotBe(first.ExecutionId);
        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(2);
        statement.PresentRecordCount.ShouldBe(2);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    [Fact]
    public async Task An_execution_that_landed_with_its_presence_unaccounted_leaves_a_visible_shortfall()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new ExecutionPresenceLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());

        var handle = await OpenAsync(planeScope.Resolve<INativeRecordPlane>(), run);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessExecution.CountAsync(candidate => candidate.Id == handle.ExecutionId)).ShouldBe(1);

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(0);
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "a lost post-commit claim must leave the declared expectation visibly short, never Exact over an unaccounted row");
    }

    [Fact]
    public async Task A_lost_later_generation_declaration_unstates_the_facet_instead_of_writing_expected_zero_exact()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var firstPlane = Plane(out var firstScope);
        using var _ = firstScope;
        var first = await OpenAsync(firstPlane, run);
        await firstPlane.CloseAsync(first, exitCode: 0, CancellationToken.None);
        await ((INativeRecordExecutionPlane)firstPlane).TerminalizeAsync(run.TeamId, run.AgentRunId, run.FenceEpoch, CancellationToken.None);

        using var secondScope = _fixture.BeginScope(builder => builder.Register<IRunDataCompletenessWriter>(context =>
                new ExecutionDeclarationLosingWriter(new RunDataCompletenessWriter(context.Resolve<IServiceScopeFactory>(), NullLogger<RunDataCompletenessWriter>.Instance)))
            .InstancePerLifetimeScope());
        var second = await OpenAsync(secondScope.Resolve<INativeRecordPlane>(), run);

        second.ExecutionId.ShouldNotBe(first.ExecutionId);
        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBeNull(
            customMessage: "the producer must not follow a lost expectation with a present-only delta, which would create Expected=0 and falsely read Exact");
        statement.PresentRecordCount.ShouldBe(1, customMessage: "the first generation's accounted presence remains a fact");
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
    }

    [Fact]
    public async Task A_refused_new_execution_this_worker_still_owns_becomes_a_bounded_identity_gap()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        await Should.ThrowAsync<DbUpdateException>(() => OpenRawAsync(plane, run, run.FenceEpoch, MalformedLocator));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var gap = await db.WorkflowRunCaptureGap.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
            && candidate.SubjectKind == WorkflowRunDataOwnerKinds.HarnessExecution);
        gap.SubjectId.ShouldNotBeNullOrWhiteSpace();
        var missingExecutionId = Guid.Parse(gap.SubjectId!);

        gap.RangeKind.ShouldBe(CaptureGapRangeKind.Unbounded);
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.Id == missingExecutionId)).ShouldBe(0,
            customMessage: "the fixed-size gap names the exact execution identity whose transaction was refused");

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(1);
        statement.PresentRecordCount.ShouldBe(0);
        statement.KnownMissingCount.ShouldBe(1);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
    }

    [Fact]
    public async Task A_refused_generation_from_a_superseded_worker_unstates_the_execution_expectation_without_inventing_a_gap()
    {
        var run = await SeedWorkflowBoundRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var first = await OpenAsync(plane, run);
        await plane.CloseAsync(first, exitCode: 0, CancellationToken.None);
        await ((INativeRecordExecutionPlane)plane).TerminalizeAsync(run.TeamId, run.AgentRunId, run.FenceEpoch, CancellationToken.None);

        using (var reclaimer = _fixture.BeginScope())
            (await reclaimer.Resolve<IAgentRunService>().ReclaimForReattachAsync(run.AgentRunId, CancellationToken.None)).ShouldBeTrue();

        await Should.ThrowAsync<DbUpdateException>(() => OpenRawAsync(plane, run, run.FenceEpoch, Locator));

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBeNull(
            customMessage: "a stale worker cannot prove its rejected next generation was owed, so the total becomes indeterminate instead of asserting a false shortfall");
        statement.PresentRecordCount.ShouldBe(1);
        statement.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId
            && candidate.SubjectKind == WorkflowRunDataOwnerKinds.HarnessExecution)).ShouldBe(0);
    }

    /// <summary>
    /// THE THIRD PRODUCER, on the run it could say nothing about. A refused new generation of a STANDALONE Agent Run is
    /// exactly as locatable a loss as a workflow-bound one — a minted execution identity with no row of its own — but
    /// while this write was gated on the run having a workflow parent it recorded NOTHING, so the run whose facet no
    /// manifest can carry was also the run whose refusal left no trace anywhere. That is the same silence the two
    /// sibling producers were opened up for, surviving in the one this plane's own nullable key had already unblocked.
    ///
    /// <para>The gap carries its OWNER and no attempt coordinate: the launch's attempt insert is the write that was
    /// refused, so the three columns naming it would reference a row that does not exist. Written down is half of it,
    /// which is why this READS — a gap an operator cannot reach is a loss recorded into a place nobody looks.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_execution_of_a_standalone_run_is_a_locatable_gap_against_its_agent_run()
    {
        var run = await SeedStandaloneRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        await Should.ThrowAsync<DbUpdateException>(() => OpenRawAsync(plane, run, run.FenceEpoch, MalformedLocator));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var gap = await db.WorkflowRunCaptureGap.AsNoTracking().SingleAsync(candidate => candidate.TeamId == run.TeamId
            && candidate.SubjectKind == WorkflowRunDataOwnerKinds.HarnessExecution);
        gap.WorkflowRunId.ShouldBeNull(customMessage: "this run has no workflow parent, and a gap keyed to an invented one would be worse than none");
        gap.AgentRunId.ShouldBe(run.AgentRunId, customMessage: "the run that owns the record is the run the gap names, and it is the only key this run has");
        gap.SubjectId.ShouldNotBeNullOrWhiteSpace(customMessage: "the minted execution identity is the coordinate that locates the row that does not exist");
        gap.HarnessExecutionId.ShouldBeNull(
            customMessage: "that column hard-references the execution row this refusal is the absence of, so naming the run may never be paid for with a dangling one");
        gap.HarnessProcessAttemptId.ShouldBeNull();
        gap.RangeKind.ShouldBe(CaptureGapRangeKind.Unbounded);
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);

        (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.Id == Guid.Parse(gap.SubjectId!))).ShouldBe(0,
            customMessage: "the span names the exact execution identity whose transaction was refused, so the row it points at must genuinely be absent");
        (await db.WorkflowRunDataManifest.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0,
            customMessage: "the manifest keying is untouched: what this closes is the gap plane's silence, not the absent statement, which is honestly indeterminate");

        var observed = (await scope.Resolve<IAgentRunService>().GetSummaryForTeamAsync(run.AgentRunId, run.TeamId, CancellationToken.None))!.CaptureGaps;
        observed.Availability.ShouldBe(AgentRunCaptureGapReadAvailability.Available);

        var shown = observed.Items.Where(candidate => candidate.SubjectKind == WorkflowRunDataOwnerKinds.HarnessExecution).ShouldHaveSingleItem(
            customMessage: "the drawer selects by Agent Run, and this gap names one — a row an operator cannot reach is a loss recorded into a place nobody looks");
        shown.Id.ShouldBe(gap.Id);
        shown.HarnessExecutionId.ShouldBeNull();
        shown.AttemptWorkerFenceEpoch.ShouldBeNull(
            customMessage: "the reader states 'we cannot say which attempt' rather than hiding the whole gap behind a coordinate that cannot exist");
    }

    [Fact]
    public async Task A_standalone_agent_records_its_execution_and_states_no_workflow_run_manifest()
    {
        var run = await SeedStandaloneRunAsync();
        var plane = Plane(out var planeScope);
        using var _ = planeScope;

        var handle = await OpenAsync(plane, run);
        handle.WorkflowRunId.ShouldBeNull();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.Id == handle.ExecutionId)).ShouldBe(1);
        (await db.WorkflowRunDataManifest.CountAsync(candidate => candidate.TeamId == run.TeamId
            && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessExecution)).ShouldBe(0);
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Facet == WorkflowRunDataOwnerKinds.HarnessExecution);
    }

    private INativeRecordPlane Plane(out ILifetimeScope scope)
    {
        scope = _fixture.BeginScope();
        return scope.Resolve<INativeRecordPlane>();
    }

    private static async Task<NativeRecordCaptureHandle> OpenAsync(INativeRecordPlane plane, SeededRun run) =>
        (await OpenRawAsync(plane, run, run.FenceEpoch, Locator)).ShouldNotBeNull();

    private static async Task<NativeRecordCaptureHandle?> OpenRawAsync(INativeRecordPlane plane, SeededRun run, long fenceEpoch, string locator) =>
        await plane.OpenAsync(new NativeRecordCaptureRequest
        {
            TeamId = run.TeamId, AgentRunId = run.AgentRunId, HarnessTypeKey = "claude-code/v2", RunnerKind = "local",
            RunnerLocatorJson = locator, WorkerFenceEpoch = fenceEpoch, Channel = NativeRecordChannel.Stdout,
        }, CancellationToken.None);

    private async Task<SeededRun> SeedWorkflowBoundRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "execution-completeness-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput>(), Enabled = true,
            });

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
            new AgentTask { Goal = "record execution identity", Harness = ClaudeCodeHarness.HarnessKind, Model = PricedModel, TimeoutSeconds = 1800 },
            teamId, workflowRunId, workflowRunId is null ? null : "implement", workflowRunId is null ? "" : "implement#1", CancellationToken.None);
        return new SeededRun(teamId, created.Id, workflowRunId ?? Guid.Empty, await runs.MarkRunningAsync(created.Id, CancellationToken.None));
    }

    private sealed record SeededRun(Guid TeamId, Guid AgentRunId, Guid WorkflowRunId, long FenceEpoch);

    private sealed class RecordingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;
        private readonly ICollection<RunDataFacetAdvance> _advances;

        public RecordingWriter(IRunDataCompletenessWriter real, ICollection<RunDataFacetAdvance> advances) { _real = real; _advances = advances; }

        public async Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
            await _real.InitializeAsync(initialization, cancellationToken).ConfigureAwait(false);

        public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken)
        {
            _advances.Add(advance);
            return await _real.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
            await _real.NoticeAsync(gap, cancellationToken).ConfigureAwait(false);

        public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            await _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ExecutionPresenceLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public ExecutionPresenceLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public async Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
            await _real.InitializeAsync(initialization, cancellationToken).ConfigureAwait(false);

        public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Facet == WorkflowRunDataOwnerKinds.HarnessExecution && advance.Present > 0
                ? false
                : await _real.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false);

        public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
            await _real.NoticeAsync(gap, cancellationToken).ConfigureAwait(false);

        public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            await _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ExecutionDeclarationLosingWriter : IRunDataCompletenessWriter
    {
        private readonly IRunDataCompletenessWriter _real;

        public ExecutionDeclarationLosingWriter(IRunDataCompletenessWriter real) => _real = real;

        public async Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) =>
            await _real.InitializeAsync(initialization, cancellationToken).ConfigureAwait(false);

        public async Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) =>
            advance.Facet == WorkflowRunDataOwnerKinds.HarnessExecution && advance.Expected > 0
                ? false
                : await _real.AdvanceAsync(advance, cancellationToken).ConfigureAwait(false);

        public async Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) =>
            await _real.NoticeAsync(gap, cancellationToken).ConfigureAwait(false);

        public async Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) =>
            await _real.UnstateExpectationAsync(teamId, workflowRunId, facet, cancellationToken).ConfigureAwait(false);
    }
}
