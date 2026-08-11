using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="PublishManifestStore"/> over real Postgres, real transactions, real row
/// locks — review hole 1: the delivery ledger's FIRST write must carry the fence exactly like its refreshes. The
/// decisive pin interleaves a genuine reclaim: a concurrent transaction holds the run's epoch bump uncommitted
/// while the zombie's first write passes its optimistic pre-check and reaches the FOR SHARE lock — the lock waits
/// out the bump, sees the new epoch, and refuses. Before the fix the same interleaving landed a durable stale
/// claim (the pre-check read the old committed epoch; the INSERT itself was fenceless).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PublishManifestFenceFlowTests
{
    private readonly PostgresFixture _fixture;

    public PublishManifestFenceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_first_write_racing_a_reclaim_never_lands()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = await SeedAgentRunAsync(teamId, fenceEpoch: 1);

        // The reclaimer: bump the epoch inside an OPEN transaction — the row lock is held, the bump uncommitted.
        using var reclaimScope = _fixture.BeginScope();
        var reclaimDb = reclaimScope.Resolve<CodeSpaceDbContext>();
        await using var reclaim = await reclaimDb.Database.BeginTransactionAsync();
        await reclaimDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET fence_epoch = 2 WHERE id = {agentRunId}");

        // The zombie: a fenced FIRST write claiming epoch 1. Its optimistic pre-check reads the COMMITTED epoch
        // (still 1 — MVCC) and passes; the FOR SHARE lock inside its insert transaction then blocks on the held row.
        var zombie = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, Upsert(teamId, summary: "stale claim"), expectedFenceEpoch: 1, CancellationToken.None);
        });

        await Task.Delay(500);   // let the zombie reach the lock (it cannot complete — the row is held)
        zombie.IsCompleted.ShouldBeFalse("the FOR SHARE read must wait out the in-flight reclaim, not run past it");

        await reclaim.CommitAsync();
        await zombie;

        (await RowsAsync(agentRunId)).ShouldBeEmpty("the locked read saw the committed bump — the stale first write refused; before the fix this row landed durably");
    }

    [Fact]
    public async Task A_current_epoch_first_write_lands_and_a_stale_refresh_cannot_overwrite_it()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = await SeedAgentRunAsync(teamId, fenceEpoch: 2);

        await UpsertAsync(agentRunId, Upsert(teamId, summary: "the reclaimer's claim"), expectedFenceEpoch: 2);

        var row = (await RowsAsync(agentRunId)).ShouldHaveSingleItem("the current-epoch first write lands (the lock path is not a deadlock)");
        row.Summary.ShouldBe("the reclaimer's claim");

        // A zombie refresh claiming the OLD epoch: the pre-check refuses before any write.
        await UpsertAsync(agentRunId, Upsert(teamId, summary: "zombie overwrite"), expectedFenceEpoch: 1);

        (await RowsAsync(agentRunId)).ShouldHaveSingleItem().Summary
            .ShouldBe("the reclaimer's claim", "a stale refresh never overwrites the owner's row");
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, long fenceEpoch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}", FenceEpoch = fenceEpoch };
        db.AgentRun.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }

    private async Task UpsertAsync(Guid agentRunId, PublishManifestUpsert input, long expectedFenceEpoch)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, input, expectedFenceEpoch, CancellationToken.None);
    }

    private static PublishManifestUpsert Upsert(Guid teamId, string summary) =>
        new() { TeamId = teamId, RepositoryAlias = "primary", RepositoryId = Guid.NewGuid(), Branch = "codespace/agent/z", ChangedFileCount = 1, PublishStateValue = PublishState.Pushed, Summary = summary };

    private async Task<IReadOnlyList<PublishManifest>> RowsAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).ToListAsync();
    }
}
