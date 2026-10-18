using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// The recurring repair of stranded bindings takes its candidates in id order, a batch a tick. A candidate that fails
/// stays a candidate — the right call, since the next tick may succeed — but with the batch read from the front every
/// time, fifty candidates that can never be repaired held the whole batch for good, and nobody behind them was ever
/// reached. The pass now walks the backlog in pages, so a failure costs one attempt and no place in the batch.
///
/// <para>A real failure, not a stub: a trigger on the repository table refuses the one update that re-points a chosen
/// repository, so the production service meets exactly what a constraint violation or a deadlock would hand it. The
/// poisoned credentials get ids that sort ahead of every random one, which is what puts them at the front of the batch.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RepairSweepFairnessFlowTests
{
    private const int MaxPasses = 50;

    private readonly PostgresFixture _fixture;
    private readonly BindingMoveKit _kit;

    public RepairSweepFairnessFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _kit = new BindingMoveKit(fixture);
    }

    [Fact]
    public async Task Credentials_that_can_never_be_repaired_do_not_starve_the_ones_behind_them()
    {
        var world = await _kit.SeedWorldAsync();
        var poisoned = new[] { await SeedStrandedAsync(world, FrontOfTheQueue()), await SeedStrandedAsync(world, FrontOfTheQueue()) };
        var healthy = await SeedStrandedAsync(world, deadId: null);

        await using var poison = await PoisonAsync(poisoned.Select(p => p.Repository));

        // One credential a pass: a batch the two poisoned ones alone would fill from the front.
        var passes = await SweepUntilNothingIsRepairedAsync(batchSize: 1);

        var repo = await _kit.LoadRepositoryAsync(healthy.Repository);

        repo.CredentialId.ShouldBe(healthy.Successor, "the credential behind the poisoned ones was reached, however many fail ahead of it");
        repo.Status.ShouldBe(RepositoryStatus.Active);
        passes.ShouldBeGreaterThanOrEqualTo(1);

        foreach (var stuck in poisoned)
            (await _kit.LoadRepositoryAsync(stuck.Repository)).CredentialId.ShouldBe(stuck.Dead, "a repair that failed leaves its binding exactly where it was — nothing half-moved");
    }

    [Fact]
    public async Task A_pass_repairs_no_more_than_its_batch_however_many_candidates_wait()
    {
        var world = await _kit.SeedWorldAsync();

        for (var i = 0; i < 3; i++) await SeedStrandedAsync(world, deadId: null);

        using var scope = _fixture.BeginScope();

        var repaired = await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 2 });

        repaired.ShouldBe(2, "bounded: the third waits for the next tick");

        await SweepUntilNothingIsRepairedAsync(batchSize: 2);
    }

    [Fact]
    public async Task A_candidate_that_has_failed_several_passes_running_is_left_alone_for_a_while()
    {
        // A failure the next tick may not repeat is worth another attempt; one that has repeated three ticks running is not
        // going to heal in the next five minutes, and trying it again every tick forever costs a query storm and a warning
        // each time. It stops being attempted for a while — and one that recovered sooner is repaired as soon as it can be.
        // Two worlds, so each candidate has an owner — and a successor — of its own: the repair follows an owner's NEWEST live connection.
        var transient = await SeedStrandedAsync(await _kit.SeedWorldAsync(), FrontOfTheQueue());
        var permanent = await SeedStrandedAsync(await _kit.SeedWorldAsync(), FrontOfTheQueue());

        await using var transientPoison = await PoisonAsync(new[] { transient.Repository });
        await using var permanentPoison = await PoisonAsync(new[] { permanent.Repository });

        for (var pass = 1; pass < SweepFailureBackoff.FailuresBeforeBackoff; pass++) await SweepOnceAsync();

        await transientPoison.DisarmAsync();
        await SweepOnceAsync();

        (await _kit.LoadRepositoryAsync(transient.Repository)).CredentialId.ShouldBe(transient.Successor, "one failure short of being set aside is still worth another attempt, which succeeded");
        (await _kit.LoadRepositoryAsync(permanent.Repository)).CredentialId.ShouldBe(permanent.Dead, "fixture check: still poisoned, so that attempt failed — its last one before it is set aside");

        await permanentPoison.DisarmAsync();
        await SweepOnceAsync();

        (await _kit.LoadRepositoryAsync(permanent.Repository)).CredentialId.ShouldBe(permanent.Dead, "failing that many passes in a row put it out of the sweep's rotation, so a pass within the window does not attempt it even though it would now succeed");
    }

    private async Task SweepOnceAsync()
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 50 });
    }

    private async Task<int> SweepUntilNothingIsRepairedAsync(int batchSize)
    {
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            using var scope = _fixture.BeginScope();

            if (await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = batchSize }) == 0) return pass;
        }

        throw new ShouldAssertException($"the repair still repaired something after {MaxPasses} passes of {batchSize}");
    }

    private sealed record Stranded(Guid Dead, Guid Successor, Guid Repository);

    /// <summary>A revoked credential of the owner's with a repository still bound to it, and a live one connected after the revocation to follow.</summary>
    private async Task<Stranded> SeedStrandedAsync(BindingMoveKit.World world, Guid? deadId)
    {
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);

        var dead = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked, id: deadId);
        var successor = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-successor", createdDate: revoked.AddDays(1));
        var repository = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        return new Stranded(dead, successor, repository);
    }

    /// <summary>An id that sorts ahead of any random one: the batch reads candidates in id order.</summary>
    private static Guid FrontOfTheQueue() => Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..12]}");

    private async Task<Poison> PoisonAsync(IEnumerable<Guid> repositoryIds)
    {
        var poison = new Poison(_fixture.ConnectionString, repositoryIds.ToList());

        await poison.ArmAsync();

        return poison;
    }

    /// <summary>A trigger refusing every update of the named repository rows, so the repair that re-points one throws from the database, as a real constraint would.</summary>
    private sealed class Poison : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<Guid> _repositoryIds;
        private readonly string _name = $"poison_{Guid.NewGuid():N}";

        public Poison(string connectionString, List<Guid> repositoryIds)
        {
            _connectionString = connectionString;
            _repositoryIds = repositoryIds;
        }

        public async Task ArmAsync()
        {
            var ids = string.Join(", ", _repositoryIds.Select(id => $"'{id}'"));

            await ExecuteAsync($"CREATE FUNCTION {_name}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'poisoned repository'; END $$ LANGUAGE plpgsql; " +
                               $"CREATE TRIGGER {_name} BEFORE UPDATE ON repository FOR EACH ROW WHEN (OLD.id IN ({ids})) EXECUTE FUNCTION {_name}();");
        }

        /// <summary>Let the poisoned rows be updated again, as a transient failure that has passed.</summary>
        public async Task DisarmAsync() => await ExecuteAsync($"DROP TRIGGER IF EXISTS {_name} ON repository; DROP FUNCTION IF EXISTS {_name}();");

        /// <summary>Disarm, then soft-delete what was poisoned: the sweep is deployment-wide, and a binding that can never be repaired must not stay a candidate for every later test.</summary>
        public async ValueTask DisposeAsync()
        {
            var ids = string.Join(", ", _repositoryIds.Select(id => $"'{id}'"));

            await ExecuteAsync($"DROP TRIGGER IF EXISTS {_name} ON repository; DROP FUNCTION IF EXISTS {_name}(); UPDATE repository SET deleted_date = now() WHERE id IN ({ids});");
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
