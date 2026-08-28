using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// What a node's swallowed storage failure leaves behind.
///
/// <para>The swallow itself is not in question. A node whose side effect already fired must settle, and a command that
/// completed must not fail over a lost copy of its own output. What was missing is the ACCOUNTING: without a gap, a
/// run reports success while content an operator expected to find is simply absent, and nothing downstream can tell
/// that run apart from one whose storage worked.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NodeOutputLossAttributionFlowTests
{
    private readonly PostgresFixture _fixture;

    public NodeOutputLossAttributionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Content_a_node_could_not_store_is_recorded_as_a_gap_against_that_node()
    {
        var run = await SeedInitializedRunAsync();

        await ReportLossAsync(run, "the-node", "The full command output could not be stored (IOException); only the capped preview was kept.");

        var gap = await GapAsync(run);
        gap.ShouldNotBeNull("a swallowed loss with no gap leaves the run reporting success over content that is simply absent");
        gap.SubjectKind.ShouldBe(WorkflowRunDataOwnerKinds.NodeOutput);
        gap.SubjectId.ShouldBe("the-node", "a gap that cannot say WHICH node lost content is not actionable");
        gap.Reason.ShouldBe(CaptureGapReason.WriteRefused);
        gap.ReasonDetail.ShouldContain("capped preview", Case.Insensitive, "the detail has to say what survived, or an operator cannot tell what they still have");
    }

    [Fact]
    public async Task A_node_that_stored_everything_leaves_no_gap()
    {
        // The other half. A gap written on the happy path would make every run indeterminate and the signal worthless.
        var run = await SeedInitializedRunAsync();

        (await GapAsync(run)).ShouldBeNull();
    }

    [Fact]
    public async Task Reporting_a_loss_never_throws_even_when_the_gap_cannot_be_written()
    {
        // This runs on the failure path by definition: a node that already lost content must still settle, so the
        // bookkeeping about the loss must not be the thing that fails it.
        var run = await SeedInitializedRunAsync();

        using var scope = _fixture.BeginScope();
        var observability = new NodeObservability(new NodeObservationBinding(
            scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactStore>(), run.RunId, "the-node", run.TeamId,
            Guid.NewGuid(), new PersistenceSecretRedactor([]), new ThrowingCompletenessWriter()));

        await Should.NotThrowAsync(() => ((INodeLossReporting)observability).NoticeContentNotStoredAsync("anything", CancellationToken.None));
    }

    [Fact]
    public async Task A_binding_with_no_completeness_writer_records_nothing_rather_than_failing()
    {
        // Every construction that predates this seam passes no writer. Those callers must keep working, and must not
        // manufacture a gap they have no run manifest to attach it to.
        var run = await SeedInitializedRunAsync();

        using var scope = _fixture.BeginScope();
        var observability = new NodeObservability(
            scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactStore>(), run.RunId, "the-node", run.TeamId, Guid.NewGuid());

        await ((INodeLossReporting)observability).NoticeContentNotStoredAsync("anything", CancellationToken.None);

        (await GapAsync(run)).ShouldBeNull();
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task ReportLossAsync(SeededRun run, string nodeId, string detail)
    {
        using var scope = _fixture.BeginScope();
        var observability = new NodeObservability(new NodeObservationBinding(
            scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactStore>(), run.RunId, nodeId, run.TeamId,
            Guid.NewGuid(), new PersistenceSecretRedactor([]), scope.Resolve<IRunDataCompletenessWriter>()));

        await ((INodeLossReporting)observability).NoticeContentNotStoredAsync(detail, CancellationToken.None);
    }

    private async Task<WorkflowRunCaptureGap?> GapAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking()
            .SingleOrDefaultAsync(gap => gap.WorkflowRunId == run.RunId && gap.SubjectKind == WorkflowRunDataOwnerKinds.NodeOutput);
    }

    private async Task<SeededRun> SeedInitializedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "node-output-loss-" + Guid.NewGuid().ToString("N")[..6],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(new RunDataManifestInitialization(teamId, runId), CancellationToken.None)).ShouldBeTrue();

        return new SeededRun(teamId, runId);
    }

    private sealed record SeededRun(Guid TeamId, Guid RunId);

    /// <summary>A writer that fails every way it can, so the reporter's containment is exercised rather than assumed.</summary>
    private sealed class ThrowingCompletenessWriter : IRunDataCompletenessWriter
    {
        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) => throw new InvalidOperationException("no");
        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) => throw new InvalidOperationException("no");
        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken) => throw new InvalidOperationException("no");
        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) => throw new InvalidOperationException("no");
    }
}
