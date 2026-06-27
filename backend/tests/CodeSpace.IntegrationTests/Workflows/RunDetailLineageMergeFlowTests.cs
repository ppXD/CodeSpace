using Autofac;
using Microsoft.EntityFrameworkCore;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
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

    private async Task<Messages.Dtos.Workflows.WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);
    }
}
