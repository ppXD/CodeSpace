using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the P1.2 rolling tape-digest store — one row per run, forward-only roll
/// (a stale writer's lower sequence never regresses the digest), team-scoped reads.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Integration")]
public class SupervisorTapeSummaryStoreTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorTapeSummaryStoreTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_digest_upserts_forward_only_and_reads_back()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var store = scope.Resolve<ISupervisorTapeSummaryStore>();

            (await store.GetAsync(runId, teamId, CancellationToken.None)).ShouldBeNull("nothing compacted yet");

            await store.UpsertAsync(runId, teamId, upToSequence: 4, "digest v1", CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var store = scope.Resolve<ISupervisorTapeSummaryStore>();

            var v1 = await store.GetAsync(runId, teamId, CancellationToken.None);
            v1.ShouldNotBeNull();
            v1!.UpToSequence.ShouldBe(4);
            v1.Text.ShouldBe("digest v1");

            // Roll forward — the rolling row advances.
            await store.UpsertAsync(runId, teamId, upToSequence: 9, "digest v2", CancellationToken.None);

            // A stale writer (lower sequence) must NOT regress the digest.
            await store.UpsertAsync(runId, teamId, upToSequence: 6, "stale digest", CancellationToken.None);
            await store.UpsertAsync(runId, teamId, upToSequence: 9, "same-sequence replacement", CancellationToken.None);
        }

        using (var verify = _fixture.BeginScope())
        {
            var final = await verify.Resolve<ISupervisorTapeSummaryStore>().GetAsync(runId, teamId, CancellationToken.None);

            final!.UpToSequence.ShouldBe(9, "forward-only: the stale lower-sequence write was ignored");
            final.Text.ShouldBe("digest v2", "equal coverage cannot replace the summary that won that sequence");
        }
    }

    [Fact]
    public async Task The_digest_is_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ISupervisorTapeSummaryStore>();

        await store.UpsertAsync(runId, teamId, upToSequence: 4, "digest", CancellationToken.None);

        (await store.GetAsync(runId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull("another team never reads this run's digest");

        await store.UpsertAsync(runId, foreignTeam, upToSequence: 99, "foreign overwrite", CancellationToken.None);
        var original = await store.GetAsync(runId, teamId, CancellationToken.None);
        original!.UpToSequence.ShouldBe(4, "the global run key cannot let another team advance this team's summary");
        original.Text.ShouldBe("digest");
    }

    [Fact]
    public async Task A_stale_tracked_writer_cannot_regress_a_newer_scope()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using (var seed = _fixture.BeginScope())
            await seed.Resolve<ISupervisorTapeSummaryStore>().UpsertAsync(runId, teamId, upToSequence: 4, "base", CancellationToken.None);

        using var newerScope = _fixture.BeginScope();
        using var staleScope = _fixture.BeginScope();
        await newerScope.Resolve<CodeSpaceDbContext>().SupervisorTapeSummaryRecord.SingleAsync(r => r.SupervisorRunId == runId);
        await staleScope.Resolve<CodeSpaceDbContext>().SupervisorTapeSummaryRecord.SingleAsync(r => r.SupervisorRunId == runId);

        await newerScope.Resolve<ISupervisorTapeSummaryStore>().UpsertAsync(runId, teamId, upToSequence: 9, "newer", CancellationToken.None);
        await staleScope.Resolve<ISupervisorTapeSummaryStore>().UpsertAsync(runId, teamId, upToSequence: 6, "stale", CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var final = await verify.Resolve<ISupervisorTapeSummaryStore>().GetAsync(runId, teamId, CancellationToken.None);
        final!.UpToSequence.ShouldBe(9, "the database must enforce forward-only summary coverage across independent worker scopes");
        final.Text.ShouldBe("newer");
    }
}
