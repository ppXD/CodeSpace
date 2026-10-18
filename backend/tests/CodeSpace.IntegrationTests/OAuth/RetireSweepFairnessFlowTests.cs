using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// The recurring retirement of departed members' tokens has the same shape as the repair of stranded bindings: it takes
/// its candidates in id order, a batch a tick, and a candidate that fails stays a candidate. Read from the front every
/// time, fifty tokens that could never be retired held the whole batch for good — and a healthy departed member's token,
/// the one the sweep exists to end, kept authenticating repositories behind them. Both sweeps now walk their backlog in
/// pages, so a failure costs one attempt and no place in the batch.
///
/// <para>A real failure, not a stub: a trigger on the credential table refuses the one update that retires a chosen
/// token, so the production service meets exactly what a constraint violation or a deadlock would hand it. The poisoned
/// tokens get ids that sort ahead of every random one, which is what puts them at the front of the batch.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetireSweepFairnessFlowTests
{
    private const int MaxPasses = 50;

    private readonly PostgresFixture _fixture;
    private readonly BindingMoveKit _kit;

    public RetireSweepFairnessFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _kit = new BindingMoveKit(fixture);
    }

    [Fact]
    public async Task Tokens_that_can_never_be_retired_do_not_starve_the_departed_token_behind_them()
    {
        var world = await _kit.SeedWorldAsync();
        var stranger = await SeedStrangerAsync();
        var poisoned = new[] { await _kit.SeedCredentialAsync(world, stranger, "glpat-poison-a", id: FrontOfTheQueue()), await _kit.SeedCredentialAsync(world, stranger, "glpat-poison-b", id: FrontOfTheQueue()) };
        var healthy = await _kit.SeedCredentialAsync(world, stranger, "glpat-healthy");

        await using var poison = await PoisonAsync(poisoned);

        // One token a pass: a batch the two poisoned ones alone would fill from the front.
        var passes = await SweepUntilRetiredAsync(healthy, batchSize: 1);

        var retired = await _kit.LoadCredentialAsync(healthy);

        retired.DeletedDate.ShouldNotBeNull("the departed member's token behind the poisoned ones was reached, however many fail ahead of it");
        retired.Status.ShouldBe(CredentialStatus.Revoked);
        retired.EncryptedPayload.ShouldBeEmpty("a retired token has nothing left to authenticate with");
        passes.ShouldBeGreaterThanOrEqualTo(1);

        foreach (var stuck in poisoned)
        {
            var credential = await _kit.LoadCredentialAsync(stuck);

            credential.DeletedDate.ShouldBeNull("a retirement that failed leaves the token exactly as it was — nothing half-done");
            credential.Status.ShouldBe(CredentialStatus.Active);
        }
    }

    [Fact]
    public async Task A_token_that_has_failed_several_passes_running_is_left_alone_for_a_while_and_one_that_recovered_sooner_is_retired()
    {
        // The same memory the repair sweep keeps (see RepairSweepFairnessFlowTests), through the retirement's own wiring: the
        // sweep runs in a fresh scope each tick, so what it remembers must outlive a scope.
        var world = await _kit.SeedWorldAsync();
        var transient = await _kit.SeedCredentialAsync(world, await SeedStrangerAsync(), "glpat-transient", id: FrontOfTheQueue());
        var permanent = await _kit.SeedCredentialAsync(world, await SeedStrangerAsync(), "glpat-permanent", id: FrontOfTheQueue());

        await using var transientPoison = await PoisonAsync(new[] { transient });
        await using var permanentPoison = await PoisonAsync(new[] { permanent });

        for (var pass = 1; pass < SweepFailureBackoff.FailuresBeforeBackoff; pass++) await SweepOnceAsync();

        await transientPoison.DisarmAsync();
        await SweepOnceAsync();

        (await _kit.LoadCredentialAsync(transient)).DeletedDate.ShouldNotBeNull("one failure short of being set aside is still worth another attempt, which succeeded");
        (await _kit.LoadCredentialAsync(permanent)).DeletedDate.ShouldBeNull("fixture check: still poisoned, so that attempt failed — its last one before it is set aside");

        await permanentPoison.DisarmAsync();
        await SweepOnceAsync();

        (await _kit.LoadCredentialAsync(permanent)).DeletedDate.ShouldBeNull("failing that many passes in a row put it out of the sweep's rotation, so a pass within the window does not attempt it even though it would now succeed");
    }

    private async Task SweepOnceAsync()
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IMediator>().Send(new RetireDepartedMembersCredentialsCommand { BatchSize = 50 });
    }

    private async Task<int> SweepUntilRetiredAsync(Guid credentialId, int batchSize)
    {
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            using var scope = _fixture.BeginScope();

            await scope.Resolve<IMediator>().Send(new RetireDepartedMembersCredentialsCommand { BatchSize = batchSize });

            if ((await _kit.LoadCredentialAsync(credentialId)).DeletedDate != null) return pass;
        }

        throw new ShouldAssertException($"the departed member's token was still live after {MaxPasses} passes of {batchSize}: the sweep never got past the tokens that fail");
    }

    /// <summary>A person with an account and no membership in the team — what a departed member is, before the sweep retires what they lent.</summary>
    private async Task<Guid> SeedStrangerAsync()
    {
        var stranger = new User { Id = Guid.NewGuid(), Email = $"stranger-{Guid.NewGuid():N}@x", Name = "stranger" };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(stranger);
        await db.SaveChangesAsync();

        return stranger.Id;
    }

    /// <summary>An id that sorts ahead of any random one: the batch reads candidates in id order.</summary>
    private static Guid FrontOfTheQueue() => Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..12]}");

    private async Task<Poison> PoisonAsync(IEnumerable<Guid> credentialIds)
    {
        var poison = new Poison(_fixture.ConnectionString, credentialIds.ToList());

        await poison.ArmAsync();

        return poison;
    }

    /// <summary>A trigger refusing every update of the named credential rows, so the retirement of one throws from the database, as a real constraint would.</summary>
    private sealed class Poison : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly List<Guid> _credentialIds;
        private readonly string _name = $"poison_{Guid.NewGuid():N}";

        public Poison(string connectionString, List<Guid> credentialIds)
        {
            _connectionString = connectionString;
            _credentialIds = credentialIds;
        }

        public async Task ArmAsync()
        {
            var ids = string.Join(", ", _credentialIds.Select(id => $"'{id}'"));

            await ExecuteAsync($"CREATE FUNCTION {_name}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'poisoned credential'; END $$ LANGUAGE plpgsql; " +
                               $"CREATE TRIGGER {_name} BEFORE UPDATE ON credential FOR EACH ROW WHEN (OLD.id IN ({ids})) EXECUTE FUNCTION {_name}();");
        }

        /// <summary>Let the poisoned rows be updated again, as a transient failure that has passed.</summary>
        public async Task DisarmAsync() => await ExecuteAsync($"DROP TRIGGER IF EXISTS {_name} ON credential; DROP FUNCTION IF EXISTS {_name}();");

        /// <summary>Disarm, then retire what was poisoned: the sweep is deployment-wide, and a token that can never be retired must not stay a candidate for every later test.</summary>
        public async ValueTask DisposeAsync()
        {
            var ids = string.Join(", ", _credentialIds.Select(id => $"'{id}'"));

            await ExecuteAsync($"DROP TRIGGER IF EXISTS {_name} ON credential; DROP FUNCTION IF EXISTS {_name}(); UPDATE credential SET deleted_date = now(), status = 'Revoked' WHERE id IN ({ids});");
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
