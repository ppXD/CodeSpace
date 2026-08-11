using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (the REAL <see cref="CaptureIntentService"/> over real Postgres): the saga's own contract —
/// idempotent open per attempt, epoch-guarded commit CAS, honest INDETERMINATE marking, and the terminal-run
/// reaper that never touches a live attempt or a settled promise. The executor wiring is pinned separately
/// (<c>AgentRunExecutorTests.The_capture_promise_commits_with_the_run</c>).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CaptureIntentSagaFlowTests
{
    private readonly PostgresFixture _fixture;

    public CaptureIntentSagaFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_open_is_idempotent_per_attempt_and_a_commit_is_epoch_guarded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var saga = scope.Resolve<ICaptureIntentService>();

        await saga.OpenAsync(agentRunId, teamId, workflowRunId: null, fenceEpoch: 1, expectationsJson: null, CancellationToken.None);
        await saga.OpenAsync(agentRunId, teamId, workflowRunId: null, fenceEpoch: 1, expectationsJson: null, CancellationToken.None);

        (await RowsAsync(agentRunId)).ShouldHaveSingleItem("a crash replay re-opens onto the same promise, never a duplicate");

        (await saga.CommitAsync(agentRunId, fenceEpoch: 2, """{"empty":true}""", CancellationToken.None))
            .ShouldBeFalse("only the epoch that opened the promise may commit it — a reclaimed attempt's commit refuses");

        (await saga.CommitAsync(agentRunId, fenceEpoch: 1, """{"empty":true}""", CancellationToken.None)).ShouldBeTrue();

        var row = (await RowsAsync(agentRunId)).Single();
        row.Status.ShouldBe(CaptureIntentStatus.Committed);
        row.FactsJson.ShouldNotBeNull();

        (await saga.CommitAsync(agentRunId, fenceEpoch: 1, """{"empty":false}""", CancellationToken.None))
            .ShouldBeFalse("a settled promise never re-commits");
    }

    [Fact]
    public async Task Recovery_marks_only_the_open_promises_indeterminate()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var saga = scope.Resolve<ICaptureIntentService>();

        await saga.OpenAsync(agentRunId, teamId, null, fenceEpoch: 1, null, CancellationToken.None);
        await saga.CommitAsync(agentRunId, fenceEpoch: 1, """{"empty":true}""", CancellationToken.None);
        await saga.OpenAsync(agentRunId, teamId, null, fenceEpoch: 2, null, CancellationToken.None);   // the reclaimed attempt's promise, still open

        (await saga.MarkIndeterminateForRunAsync(agentRunId, CancellationToken.None)).ShouldBe(1);

        var rows = await RowsAsync(agentRunId);
        rows.Single(r => r.FenceEpoch == 1).Status.ShouldBe(CaptureIntentStatus.Committed, "a settled promise is never rewritten");
        rows.Single(r => r.FenceEpoch == 2).Status.ShouldBe(CaptureIntentStatus.Indeterminate, "the attempt died inside its window — honest-unknown, visible forever");
    }

    [Fact]
    public async Task A_confirmed_observation_supersedes_the_earlier_indeterminate_promise()
    {
        // P2 slice 2: an exactly-once guard must not eat a late truth — the first attempt died mid-window
        // (Indeterminate), the re-attach at a bumped epoch ran the capture to its persist; the confirmed commit
        // formally resolves the unknown with a POINTER, never a rewrite.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var saga = scope.Resolve<ICaptureIntentService>();

        await saga.OpenAsync(agentRunId, teamId, null, fenceEpoch: 1, null, CancellationToken.None);
        await saga.MarkIndeterminateForRunAsync(agentRunId, CancellationToken.None);

        await saga.OpenAsync(agentRunId, teamId, null, fenceEpoch: 2, null, CancellationToken.None);
        (await saga.CommitAsync(agentRunId, fenceEpoch: 2, """{"empty":false}""", CancellationToken.None)).ShouldBeTrue();

        var rows = await RowsAsync(agentRunId);
        var confirmed = rows.Single(r => r.FenceEpoch == 2);
        var unknown = rows.Single(r => r.FenceEpoch == 1);

        unknown.Status.ShouldBe(CaptureIntentStatus.Indeterminate, "the supersede is a pointer, never a rewrite — history intact");
        unknown.SupersededByIntentId.ShouldBe(confirmed.Id, "the run's capture state now reads resolved-by, not permanently unknown");
        confirmed.SupersededByIntentId.ShouldBeNull();
    }

    [Fact]
    public async Task An_unsuperseded_indeterminate_stays_a_visible_unknown()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var saga = scope.Resolve<ICaptureIntentService>();

        await saga.OpenAsync(agentRunId, teamId, null, fenceEpoch: 1, null, CancellationToken.None);
        await saga.MarkIndeterminateForRunAsync(agentRunId, CancellationToken.None);

        (await RowsAsync(agentRunId)).Single().SupersededByIntentId
            .ShouldBeNull("no confirmed observation ever arrived — the unknown stays visible");
    }

    [Fact]
    public async Task The_reaper_marks_dangling_promises_of_terminal_runs_only()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var terminalRun = await SeedAgentRunAsync(teamId, AgentRunStatus.Failed);
        var liveRun = await SeedAgentRunAsync(teamId, AgentRunStatus.Running);

        using var scope = _fixture.BeginScope();
        var saga = scope.Resolve<ICaptureIntentService>();

        await saga.OpenAsync(terminalRun, teamId, null, fenceEpoch: 1, null, CancellationToken.None);
        await saga.OpenAsync(liveRun, teamId, null, fenceEpoch: 1, null, CancellationToken.None);

        (await saga.SweepDanglingForTerminalRunsAsync(batchSize: 50, CancellationToken.None)).ShouldBe(1);

        (await RowsAsync(terminalRun)).Single().Status.ShouldBe(CaptureIntentStatus.Indeterminate, "the run landed terminal with its promise unresolved — an ordering recovery missed");
        (await RowsAsync(liveRun)).Single().Status.ShouldBe(CaptureIntentStatus.Intended, "a live attempt's window is still open — the reaper never touches it");
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, AgentRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = status, TaskJson = "{}" };
        db.AgentRun.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    private async Task<IReadOnlyList<CaptureIntent>> RowsAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().CaptureIntent.AsNoTracking()
            .Where(i => i.AgentRunId == agentRunId).OrderBy(i => i.FenceEpoch).ToListAsync();
    }
}
