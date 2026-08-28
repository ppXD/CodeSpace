using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The exclusion itself, against a real Postgres — the only place it exists. <c>pg_advisory_xact_lock</c> is a database
/// behaviour, so a fake would only re-assert the assumption under test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorageBootstrapLockTests
{
    private readonly PostgresFixture _fixture;

    public StorageBootstrapLockTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_bootstraps_of_one_team_are_serialised()
    {
        // The property the deployment-default materializer and AgentRunLogStorageReadiness both depend on: whichever
        // runs second cannot read "this team has no route" until the first has COMMITTED the route it decided to make.
        var teamId = Guid.NewGuid();
        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await StorageBootstrapLock.TakeAsync(db.Database, teamId, CancellationToken.None);
            held.SetResult();
            await release.Task;
            await transaction.CommitAsync();
        });

        await held.Task;

        var second = SecondTakeAsync(teamId);
        var raced = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));

        raced.ShouldNotBe(second, "the second bootstrap took the lock while the first still held it — the two would then both read 'no route' and both build a profile, and the loser's profile and credential can never be deleted");

        release.SetResult();
        await first;
        await second.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Two_bootstraps_of_different_teams_do_not_block_each_other()
    {
        // The other half. A lock that serialised every team would turn team creation into a global queue, so the test
        // that proves exclusion is only meaningful next to the one that proves it is scoped.
        var held = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await StorageBootstrapLock.TakeAsync(db.Database, Guid.NewGuid(), CancellationToken.None);
            held.SetResult();
            await release.Task;
            await transaction.CommitAsync();
        });

        await held.Task;

        await SecondTakeAsync(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(10));

        release.SetResult();
        await first;
    }

    /// <summary>Takes and immediately releases the lock in its own transaction, on its own connection.</summary>
    private async Task SecondTakeAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StorageBootstrapLock.TakeAsync(db.Database, teamId, CancellationToken.None);
        await transaction.CommitAsync();
    }
}
