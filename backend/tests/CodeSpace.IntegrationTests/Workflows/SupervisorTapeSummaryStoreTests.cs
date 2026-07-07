using Autofac;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
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
        }

        using (var verify = _fixture.BeginScope())
        {
            var final = await verify.Resolve<ISupervisorTapeSummaryStore>().GetAsync(runId, teamId, CancellationToken.None);

            final!.UpToSequence.ShouldBe(9, "forward-only: the stale lower-sequence write was ignored");
            final.Text.ShouldBe("digest v2");
        }
    }

    [Fact]
    public async Task The_digest_is_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ISupervisorTapeSummaryStore>();

        await store.UpsertAsync(runId, teamId, upToSequence: 4, "digest", CancellationToken.None);

        (await store.GetAsync(runId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull("another team never reads this run's digest");
    }
}
