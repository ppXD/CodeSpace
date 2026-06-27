using System.Text.Json;
using Autofac;
using Microsoft.EntityFrameworkCore;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the LINEAGE-MERGED run detail (<see cref="IWorkflowService.GetRunAsync"/>): a rerun fork only re-executes the
/// chosen branch, so opening the latest attempt must still show the WHOLE fan-out — every branch from the latest
/// attempt that ran it (the re-run one fresh, the reused ones from an earlier attempt). Real DB (the merge is a query),
/// so this is the integration tier.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunDetailLineageMergeFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunDetailLineageMergeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_latest_attempt_shows_the_FULL_fan_out_merging_reused_branches_from_the_earlier_attempt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        // Attempt 1 (the replay) ran ALL 3 branches; attempt 2 (the rerun) re-ran only branch 0.
        var replay = await SeedRunAsync(teamId, parent: null, root: null, created: t.AddMinutes(-5));
        await SeedMapAsync(replay, branches: 3, rerunBranchesOnly: null);

        var rerun = await SeedRunAsync(teamId, parent: replay, root: replay, created: t);
        await SeedMapAsync(rerun, branches: 3, rerunBranchesOnly: new[] { 0 });   // only branch 0 physically on the fork

        var detail = await GetRunAsync(rerun, teamId);

        detail.ShouldNotBeNull();
        var branchKeys = detail!.Nodes.Where(n => n.IterationKey.StartsWith("map#")).Select(n => n.IterationKey).OrderBy(k => k).ToArray();
        branchKeys.ShouldBe(new[] { "map#0", "map#1", "map#2" });   // the merged fan-out shows ALL 3 branches, not just the one re-run

        // The re-run branch links to the fork's agent run; the reused branches to the earlier attempt's.
        var byKey = detail.Nodes.ToDictionary(n => n.IterationKey);
        byKey["map#0"].AgentRunId.ShouldBe(AgentTokenFor(rerun, 0), "the re-run branch shows the LATEST attempt's agent run");
        byKey["map#1"].AgentRunId.ShouldBe(AgentTokenFor(replay, 1), "a reused branch links to its agent run on the earlier attempt");
        byKey["map#2"].AgentRunId.ShouldBe(AgentTokenFor(replay, 2));
        byKey["map#0"].Status.ShouldBe(NodeStatus.Success, "the re-run branch's row comes from the fork (the latest attempt)");
    }

    [Fact]
    public async Task When_the_fork_re_emits_every_branch_the_latest_attempts_copy_wins_each_cell()
    {
        // Production fidelity: a real rerun RE-EMITS the reused siblings onto the fork (SeedSiblingBranchCells), so BOTH
        // attempts carry all 3 branches. The merge must then pick the LATEST attempt's copy for every contested cell.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        var replay = await SeedRunAsync(teamId, parent: null, root: null, created: t.AddMinutes(-5));
        await SeedMapAsync(replay, branches: 3, rerunBranchesOnly: null);

        var rerun = await SeedRunAsync(teamId, parent: replay, root: replay, created: t);
        await SeedMapAsync(rerun, branches: 3, rerunBranchesOnly: null);   // the fork carries ALL branches too

        var detail = await GetRunAsync(rerun, teamId);

        var byKey = detail!.Nodes.Where(n => n.IterationKey.StartsWith("map#")).ToDictionary(n => n.IterationKey);
        byKey.Count.ShouldBe(3, "still exactly 3 branches — no duplicate cells across attempts");
        for (var i = 0; i < 3; i++)
            byKey[$"map#{i}"].AgentRunId.ShouldBe(AgentTokenFor(rerun, i), $"branch {i} resolves to the LATEST attempt's copy, not the older one");
    }

    [Fact]
    public async Task Cell_attempt_history_returns_every_attempt_that_ran_the_cell_oldest_first_latest_flagged()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        // Branch 0 FAILED on attempt 1, then a rerun SUCCEEDED it on attempt 2 — the cell history must surface both.
        var a1 = await SeedRunAsync(teamId, parent: null, root: null, created: t.AddMinutes(-5));
        await SeedCellAsync(a1, "map", "map#0", WorkflowRunRecordTypes.NodeFailed);
        var a2 = await SeedRunAsync(teamId, parent: a1, root: a1, created: t);
        await SeedCellAsync(a2, "map", "map#0", WorkflowRunRecordTypes.NodeCompleted);

        using var scope = _fixture.BeginScope();
        var hist = await scope.Resolve<IWorkflowService>().ListCellAttemptsAsync(a2, "map", "map#0", teamId, CancellationToken.None);

        hist.ShouldNotBeNull();
        hist!.Attempts.Select(x => (x.AttemptNumber, x.Status, x.IsLatest)).ShouldBe(new[] { (1, NodeStatus.Failure, false), (2, NodeStatus.Success, true) });
        hist.Attempts[0].AgentRunId.ShouldBe(AgentTokenFor(a1, 0), "the earlier (failed) attempt links to its own agent run");
        hist.Attempts[1].AgentRunId.ShouldBe(AgentTokenFor(a2, 0));
    }

    [Fact]
    public async Task Each_cell_attempt_carries_its_OWN_metrics_so_looking_back_shows_that_attempts_spend_not_the_latest()
    {
        // The reported bug: a failed/earlier attempt showed NO tokens and the LATEST attempt's time. Each CellAttempt now
        // carries its own agent run's metrics, so the per-cell history must surface DIFFERENT figures per attempt.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        var a1 = await SeedRunAsync(teamId, parent: null, root: null, created: t.AddMinutes(-5));
        await SeedCellWithAgentAsync(a1, teamId, "map", "map#0", AgentRunStatus.Failed, WorkflowRunRecordTypes.NodeFailed, input: 2500, output: 500, model: "claude-sonnet", durationMs: 45_000, files: 1);

        var a2 = await SeedRunAsync(teamId, parent: a1, root: a1, created: t);
        await SeedCellWithAgentAsync(a2, teamId, "map", "map#0", AgentRunStatus.Succeeded, WorkflowRunRecordTypes.NodeCompleted, input: 12000, output: 2200, model: "claude-opus-4-8", durationMs: 137_000, files: 3);

        using var scope = _fixture.BeginScope();
        var hist = await scope.Resolve<IWorkflowService>().ListCellAttemptsAsync(a2, "map", "map#0", teamId, CancellationToken.None);

        var first = hist!.Attempts[0];
        first.InputTokens.ShouldBe(2500);
        first.OutputTokens.ShouldBe(500);
        first.Model.ShouldBe("claude-sonnet");
        first.DurationMs.ShouldBe(45_000);
        first.FilesChanged.ShouldBe(1);

        var latest = hist.Attempts[1];
        latest.InputTokens.ShouldBe(12000, "the latest attempt's metrics are its own, distinct from the earlier attempt's");
        latest.OutputTokens.ShouldBe(2200);
        latest.Model.ShouldBe("claude-opus-4-8");
        latest.DurationMs.ShouldBe(137_000);
        latest.FilesChanged.ShouldBe(3);
        latest.CostUsd.ShouldNotBeNull("a priced model yields a per-attempt cost");
    }

    [Fact]
    public async Task A_never_rerun_run_is_unchanged_by_the_merge()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lone = await SeedRunAsync(teamId, parent: null, root: null, created: DateTimeOffset.UtcNow);
        await SeedMapAsync(lone, branches: 2, rerunBranchesOnly: null);

        var detail = await GetRunAsync(lone, teamId);

        detail!.Nodes.Where(n => n.IterationKey.StartsWith("map#")).Select(n => n.IterationKey).OrderBy(k => k).ToArray()
            .ShouldBe(new[] { "map#0", "map#1" });   // a single-attempt run shows exactly its own branches — the merge is a no-op
    }

    private static string AgentTokenFor(Guid runId, int branch) => $"{runId:N}-agent-{branch}";

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid? parent, Guid? root, DateTimeOffset created)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = root == null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = created, VerifiedAt = created, NormalizedAt = created,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = root == null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            ParentRunId = parent, RootRunId = root, Status = WorkflowRunStatus.Suspended,
            CreatedDate = created, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return runId;
    }

    /// <summary>Seed a top-level "map" node + its branch cells via the ledger (workflow_run_node is a VIEW over node.* records). When <paramref name="rerunBranchesOnly"/> is set, ONLY those branch cells are written (a fork that re-ran a subset); else all branches.</summary>
    private async Task SeedMapAsync(Guid runId, int branches, int[]? rerunBranchesOnly)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var indices = rerunBranchesOnly ?? Enumerable.Range(0, branches).ToArray();
        var seq = 0;

        db.WorkflowRunRecord.Add(NodeRecord(runId, "map", "", ++seq, now));   // the top-level map aggregate

        foreach (var i in indices)
        {
            db.WorkflowRunRecord.Add(NodeRecord(runId, "map", $"map#{i}", ++seq, now));
            db.WorkflowRunWait.Add(new WorkflowRunWait
            {
                Id = Guid.NewGuid(), RunId = runId, NodeId = "map", IterationKey = $"map#{i}", WaitKind = WorkflowWaitKinds.AgentRun,
                Token = AgentTokenFor(runId, i), WakeAt = now.AddMinutes(5), Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now, ResolvedAt = now,
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>A node.completed ledger record — the view projects it to a Success node cell for (run, node, iteration).</summary>
    private static WorkflowRunRecord NodeRecord(Guid runId, string nodeId, string iterationKey, int sequence, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), RunId = runId, Sequence = sequence, RecordType = WorkflowRunRecordTypes.NodeCompleted,
        NodeId = nodeId, IterationKey = iterationKey, CorrelationId = null, PayloadJson = "{}", OccurredAt = at,
    };

    /// <summary>Seed ONE cell with a given outcome (node.* record) + its agent run wait — for the per-cell history tests.</summary>
    private async Task SeedCellAsync(Guid runId, string nodeId, string iterationKey, string recordType)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, Sequence = 1, RecordType = recordType,
            NodeId = nodeId, IterationKey = iterationKey, CorrelationId = null, PayloadJson = "{}", OccurredAt = now,
        });
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = nodeId, IterationKey = iterationKey, WaitKind = WorkflowWaitKinds.AgentRun,
            Token = AgentTokenFor(runId, 0), WakeAt = now.AddMinutes(5), Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now, ResolvedAt = now,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Seed a cell whose agent run is a REAL <c>agent_run</c> row (Guid token) carrying token usage + model + timing, so <c>ListCellAttemptsAsync</c> can fold its OWN metrics. The wait token is the agent run's Guid (what the metric join parses).</summary>
    private async Task SeedCellWithAgentAsync(Guid runId, Guid teamId, string nodeId, string iterationKey, AgentRunStatus agentStatus, string recordStatus, int input, int output, string model, long durationMs, int files)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, Sequence = 1, RecordType = recordStatus,
            NodeId = nodeId, IterationKey = iterationKey, CorrelationId = null, PayloadJson = "{}", OccurredAt = now,
        });
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = nodeId, IterationKey = iterationKey, WaitKind = WorkflowWaitKinds.AgentRun,
            Token = agentRunId.ToString(), WakeAt = now.AddMinutes(5), Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now, ResolvedAt = now,
        });
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = nodeId, IterationKey = iterationKey,
            Harness = "claude-code", Status = agentStatus,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "do the thing", Harness = "claude-code", Model = model }, AgentJson.Options),
            ResultJson = JsonSerializer.Serialize(new AgentRunResult
            {
                Status = agentStatus, ExitReason = "completed",
                TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output },
                ChangedFiles = Enumerable.Range(0, files).Select(i => $"file{i}.cs").ToList(),
            }, AgentJson.Options),
            StartedAt = now.AddMilliseconds(-durationMs), CompletedAt = now,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Messages.Dtos.Workflows.WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);
    }
}
