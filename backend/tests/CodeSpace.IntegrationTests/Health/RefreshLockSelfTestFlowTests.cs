using CodeSpace.Core.Services.Health;
using CodeSpace.Core.Services.Health.Sessions;
using CodeSpace.Core.Settings.Database;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Health;

/// <summary>
/// The refresh-lock self-test against a real Postgres, through the production <see cref="NpgsqlAdvisoryLockSessions"/> that
/// opens its connections from the same connection string <c>PostgresAdvisoryLock</c> does. Two real sessions must refuse each
/// other; two "clients" that share one session — what a transaction-mode pooler hands out — must be caught, because on that
/// session <c>pg_advisory_lock</c> is re-entrant and refresh quietly stops being serialized.
///
/// <para>Tier: 🟡 Medium-mock (Rule 12) for the pooler: the topology that breaks the lock cannot be stood up without
/// installing one, so "shares a session" is modelled by handing the SAME real session to both roles. Everything else — the
/// server, the connections, the lock calls, the self-test — is production.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RefreshLockSelfTestFlowTests
{
    private readonly PostgresFixture _fixture;

    public RefreshLockSelfTestFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Two_real_sessions_refuse_each_other_so_the_lock_is_exclusive_and_nothing_is_left_held()
    {
        var before = await AdvisoryLocksHeldInThisDatabaseAsync();

        var verdict = await NewSelfTest(RealSessions(_fixture.ConnectionString)).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Exclusive, verdict.Detail);
        (await AdvisoryLocksHeldInThisDatabaseAsync()).ShouldBe(before, "the test closed its sessions, which released every key it took");
    }

    [Fact]
    public async Task Two_clients_that_share_one_real_session_are_reported_as_not_exclusive()
    {
        var before = await AdvisoryLocksHeldInThisDatabaseAsync();

        await using (var shared = await RealSessions(_fixture.ConnectionString).OpenAsync(CancellationToken.None))
        {
            var verdict = await NewSelfTest(new OneSessionForEveryone(shared)).VerdictAsync(CancellationToken.None);

            verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive, "a session may take its own advisory lock again, so the second client was not refused");
            verdict.Detail.ShouldContain("PgBouncer");
        }

        (await AdvisoryLocksHeldInThisDatabaseAsync()).ShouldBe(before, "the shared session's lock was taken twice and must have been given back twice");
    }

    [Fact]
    public async Task The_production_session_behaves_as_the_self_test_assumes()
    {
        var key = Random.Shared.NextInt64();
        var sessions = RealSessions(_fixture.ConnectionString);

        await using var first = await sessions.OpenAsync(CancellationToken.None);
        await using var second = await sessions.OpenAsync(CancellationToken.None);

        await first.LockAsync(key, CancellationToken.None);

        (await second.TryLockAsync(key, CancellationToken.None)).ShouldBeFalse("another session holds it");
        (await first.TryLockAsync(key, CancellationToken.None)).ShouldBeTrue("the holder may take its own lock again — the property a shared session turns into a non-lock");

        (await first.UnlockAsync(key, CancellationToken.None)).ShouldBeTrue("the holder released a hold it had");
        (await first.UnlockAsync(key, CancellationToken.None)).ShouldBeTrue("it held the key twice");
        (await first.UnlockAsync(key, CancellationToken.None)).ShouldBeFalse("a third unlock releases nothing: Postgres answers false, which is how a pooler's misrouted unlock shows itself");

        (await second.TryLockAsync(key, CancellationToken.None)).ShouldBeTrue("released by both unlocks");

        (await second.UnlockAsync(key, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unlock_that_lands_on_another_real_session_is_reported_as_not_exclusive_even_though_the_contender_was_refused()
    {
        // Medium-mock (Rule 12): what a statement-level pooler does to the holder's unlock, modelled by sending it down a different real session.
        var before = await AdvisoryLocksHeldInThisDatabaseAsync();
        var real = RealSessions(_fixture.ConnectionString);

        await using (var holderServer = await real.OpenAsync(CancellationToken.None))
        await using (var elsewhere = await real.OpenAsync(CancellationToken.None))
        await using (var contender = await real.OpenAsync(CancellationToken.None))
        {
            var verdict = await NewSelfTest(new MisroutedUnlocks(holderServer, elsewhere, contender)).VerdictAsync(CancellationToken.None);

            verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive, "Postgres told the holder's unlock that its session did not hold the key");
            verdict.Detail.ShouldContain("did not hold");
        }

        (await AdvisoryLocksHeldInThisDatabaseAsync()).ShouldBe(before, "closing the sessions released the lock the misrouted unlocks left behind");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_inconclusive_at_once_and_names_no_address()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 3 }.ConnectionString;

        var verdict = await NewSelfTest(RealSessions(unreachable)).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Inconclusive);
        verdict.Detail.ShouldNotContain("127.0.0.1");
    }

    private static NpgsqlAdvisoryLockSessions RealSessions(string connectionString) =>
        new(new CodeSpaceConnectionString(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CodeSpaceStore:ConnectionString"] = connectionString }).Build()));

    private static RefreshLockSelfTest NewSelfTest(IAdvisoryLockSessions sessions) =>
        new(sessions, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<RefreshLockSelfTest>.Instance);

    /// <summary>The advisory locks any session holds in the fixture's own database. The suite's tests in this collection run one at a time, so before and after are comparable.</summary>
    private async Task<long> AdvisoryLocksHeldInThisDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The self-test's holder takes its lock on <c>holder</c> but every unlock it sends runs on <c>elsewhere</c>; the contender is its own real session. None is disposed here — the test owns them.</summary>
    private sealed class MisroutedUnlocks(IAdvisoryLockSession holder, IAdvisoryLockSession elsewhere, IAdvisoryLockSession contender) : IAdvisoryLockSessions
    {
        private int _opened;

        public Task<IAdvisoryLockSession> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IAdvisoryLockSession>(++_opened == 1 ? new Misrouted(holder, elsewhere) : new Borrowed(contender));

        private sealed class Misrouted(IAdvisoryLockSession lockSession, IAdvisoryLockSession unlockSession) : IAdvisoryLockSession
        {
            public Task LockAsync(long key, CancellationToken cancellationToken) => lockSession.LockAsync(key, cancellationToken);
            public Task<bool> TryLockAsync(long key, CancellationToken cancellationToken) => lockSession.TryLockAsync(key, cancellationToken);
            public Task<bool> UnlockAsync(long key, CancellationToken cancellationToken) => unlockSession.UnlockAsync(key, cancellationToken);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Both roles of the self-test get the one session — the connection a transaction-mode pooler would have handed to both clients. Not disposed by either; the test owns it.</summary>
    private sealed class OneSessionForEveryone(IAdvisoryLockSession session) : IAdvisoryLockSessions
    {
        public Task<IAdvisoryLockSession> OpenAsync(CancellationToken cancellationToken) => Task.FromResult<IAdvisoryLockSession>(new Borrowed(session));
    }

    /// <summary>A session lent to the self-test: disposing it leaves the real one open for its owner.</summary>
    private sealed class Borrowed(IAdvisoryLockSession inner) : IAdvisoryLockSession
    {
        public Task LockAsync(long key, CancellationToken cancellationToken) => inner.LockAsync(key, cancellationToken);
        public Task<bool> TryLockAsync(long key, CancellationToken cancellationToken) => inner.TryLockAsync(key, cancellationToken);
        public Task<bool> UnlockAsync(long key, CancellationToken cancellationToken) => inner.UnlockAsync(key, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
