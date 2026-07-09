using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Trace;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the live ledger tail (the Room SSE relay's source): <see cref="IRunRecordStreamer"/> yields every
/// workflow_run_record row beyond a cursor in Sequence order and STOPS at a terminal run record; it honors the cursor
/// (exclusive — a resuming client never re-receives a row); and it is team-scoped — a foreign team tails nothing.
/// Read-only against the REAL ledger + real team boundary, so a tenancy or ordering regression fails here.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordStreamerFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordStreamerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Tails_records_after_the_cursor_in_sequence_order_and_stops_at_a_terminal_run_record()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var correlationId = Guid.NewGuid();
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionStarted, "llm1", "llm1#0", correlationId, null, Payload(new { kind = "llm.complete" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", correlationId, null, Payload(new { ordinal = 0, text = "hello there" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionCompleted, "llm1", "llm1#0", correlationId, null, Payload(new { output = "hello there" }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), outputsPresent: true, CancellationToken.None);
        }

        var records = await TailAsync(teamId, userId, runId, after: 0);

        records.Select(r => r.Sequence).ShouldBeInOrder(SortDirection.Ascending, "the tail yields rows in ledger Sequence order");
        records[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.RunCompleted, "the tail STOPS at the terminal run record — it does not hang");
        records.ShouldContain(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta, "the interaction.delta row is streamed");

        var delta = records.First(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta);
        JsonDocument.Parse(delta.PayloadJson).RootElement.GetProperty("ordinal").GetInt32().ShouldBe(0);
        delta.CorrelationId.ShouldBe(correlationId, "the streamed delta carries its correlation id for the consumer to group by");
    }

    [Fact]
    public async Task Yields_only_records_strictly_after_the_given_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", Guid.NewGuid(), null, Payload(new { ordinal = 0 }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var all = await TailAsync(teamId, userId, runId, after: 0);
        var midCursor = all[all.Count / 2].Sequence;

        var after = await TailAsync(teamId, userId, runId, after: midCursor);

        after.ShouldAllBe(r => r.Sequence > midCursor, "the cursor is EXCLUSIVE — a resuming client never re-receives a row it already saw");
    }

    [Fact]
    public async Task A_foreign_team_tails_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamA, userA);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var records = await TailAsync(teamB, userB, runId, after: 0);

        records.ShouldBeEmpty("a foreign run yields nothing — the run precheck IS the tenancy boundary");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "sse-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<IReadOnlyList<RunRecordView>> TailAsync(Guid teamId, Guid userId, Guid runId, long after)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var streamer = scope.Resolve<IRunRecordStreamer>();

        var records = new List<RunRecordView>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // safety net — a correct tail stops itself at the terminal record
        await foreach (var r in streamer.TailAsync(runId, after, cts.Token))
            records.Add(r);

        return records;
    }
}
