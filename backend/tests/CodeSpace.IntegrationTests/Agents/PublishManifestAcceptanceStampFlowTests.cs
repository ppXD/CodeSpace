using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="PublishManifestStore"/> over real Postgres — pins the narrow
/// <c>StampAcceptanceForAgentRunAsync</c> write-back seam (B-pre): the stamp touches ONLY the acceptance verdict of
/// the target run's Agent-kind rows (every alias of a multi-repo unit), never another run's rows and never any other
/// field, and it is UPDATE-only — a run that never published has nothing to stamp, so no row is ever invented.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PublishManifestAcceptanceStampFlowTests
{
    private readonly PostgresFixture _fixture;

    public PublishManifestAcceptanceStampFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(PublishAcceptanceState.Passed)]
    [InlineData(PublishAcceptanceState.Failed)]
    public async Task The_stamp_writes_every_row_of_the_target_run_and_no_other(PublishAcceptanceState state)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        var targetAgent = Guid.NewGuid();
        var otherAgent = Guid.NewGuid();

        await SeedAsync(teamId, runId, targetAgent, alias: "primary", branch: "codespace/agent/a1");
        await SeedAsync(teamId, runId, targetAgent, alias: "docs", branch: "codespace/agent/a1-docs");
        await SeedAsync(teamId, runId, otherAgent, alias: "primary", branch: "codespace/agent/a2");

        await StampAsync(targetAgent, state);

        var target = await RowsAsync(targetAgent);
        target.Count.ShouldBe(2);
        target.ShouldAllBe(m => m.AcceptanceState == state, "a multi-repo unit's rows all carry the unit's single all-or-nothing verdict");
        target.Single(m => m.RepositoryAlias == "primary").Branch.ShouldBe("codespace/agent/a1", "the stamp touches ONLY the verdict — every other field survives");

        (await RowsAsync(otherAgent)).Single().AcceptanceState
            .ShouldBe(PublishAcceptanceState.NotApplicable, "a sibling unit's rows are untouched");
    }

    [Fact]
    public async Task A_run_with_no_manifest_rows_stamps_nothing_and_invents_none()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var neverPublished = Guid.NewGuid();

        await StampAsync(neverPublished, PublishAcceptanceState.Passed);

        (await RowsAsync(neverPublished)).ShouldBeEmpty("UPDATE-only — nothing was ever published, so there is nothing to stamp");
    }

    private async Task SeedAsync(Guid teamId, Guid runId, Guid agentRunId, string alias, string branch)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryAlias = alias, RepositoryId = Guid.NewGuid(),
            Branch = branch, ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);
    }

    private async Task StampAsync(Guid agentRunId, PublishAcceptanceState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().StampAcceptanceForAgentRunAsync(agentRunId, state, CancellationToken.None);
    }

    private async Task<IReadOnlyList<CodeSpace.Core.Persistence.Entities.PublishManifest>> RowsAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).OrderBy(m => m.RepositoryAlias).ToListAsync();
    }
}
