using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Settings.Database;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// 🟢 High fidelity: the refresh lock is a Postgres session lock, and a session lock lives exactly as long as its session.
/// The lock's connection must therefore be one whose disposal ends the session. A pooled connection's disposal only hands
/// it back to the pool — still holding any lock it holds — so a lock acquired the instant its caller was cancelled (the
/// webhook path gives up at five seconds) sat on an idle pooled connection, and every later refresh of that credential
/// queued behind it until the pool pruned the connection.
///
/// <para>The real <see cref="PostgresAdvisoryLock"/>, resolved from the production container, against real Postgres.
/// Lock keys are random, so no other test's credential is touched.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RefreshLockReleaseFlowTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RefreshLockReleaseFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task A_released_lock_leaves_no_server_session_behind_that_could_still_hold_it()
    {
        var key = NewKey();
        using var scope = _fixture.BeginScope();
        int holderPid;

        await using (var handle = await scope.Resolve<ICrossProcessLock>().AcquireAsync(key, CancellationToken.None).ConfigureAwait(false))
        {
            holderPid = await HolderPidAsync(key).ConfigureAwait(false);
        }

        await WaitForAsync(async () => !await BackendExistsAsync(holderPid).ConfigureAwait(false), $"the lock's session (backend {holderPid}) to end once the handle was disposed — it is idle in the connection pool instead, still able to hold a lock a failed unlock left on it; inspect `SELECT pid, state, state_change FROM pg_stat_activity WHERE pid = {holderPid}`").ConfigureAwait(false);

        (await IsHeldAsync(key).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_acquire_waits_for_the_first_to_be_released_and_then_gets_the_lock()
    {
        var key = NewKey();
        using var scope = _fixture.BeginScope();
        var locks = scope.Resolve<ICrossProcessLock>();
        var first = await locks.AcquireAsync(key, CancellationToken.None).ConfigureAwait(false);

        var second = locks.AcquireAsync(key, CancellationToken.None);
        await Task.Delay(300).ConfigureAwait(false);

        second.IsCompleted.ShouldBeFalse("the lock is held, so a second caller must wait for it");

        await first.DisposeAsync().ConfigureAwait(false);
        await using var handle = await second.WaitAsync(Patience).ConfigureAwait(false);

        (await IsHeldAsync(key).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Multiplexing=true")]                                      // multiplexing needs pooling, which the lock's session must not use
    [InlineData(";Minimum Pool Size=2;Maximum Pool Size=20")]
    [InlineData(";Maximum Pool Size=1")]
    [InlineData(";No Reset On Close=true")]
    [InlineData(";Connection Idle Lifetime=30;Connection Pruning Interval=5")]
    public async Task The_lock_is_acquired_and_released_whatever_pooling_options_the_configured_connection_string_sets(string extraOptions)
    {
        // The lock opens its session from the deployed connection string with pooling turned off. Options that only make
        // sense with a pool must not turn that into a failure: every refresh takes this lock, so one that cannot be acquired
        // fails every refresh before the provider is asked.
        var key = NewKey();
        var locks = new PostgresAdvisoryLock(ConnectionStringWith(extraOptions));

        await using (await locks.AcquireAsync(key, CancellationToken.None).ConfigureAwait(false))
        {
            (await IsHeldAsync(key).ConfigureAwait(false)).ShouldBeTrue();
        }

        (await ReleasedSoonAsync(key).ConfigureAwait(false)).ShouldBeTrue($"the lock on key {key} was still held after its handle was disposed");
    }

    [Fact]
    public async Task An_acquire_cancelled_as_the_lock_is_granted_never_leaves_it_held()
    {
        // The holder lets go and the waiter's token is cancelled within a few microseconds of each other, in either order.
        // Whatever the waiter ends up with — the handle, or the cancellation — nothing may still hold the key afterwards.
        // Before the lock's session stopped being pooled this leaked on a small fraction of runs, so this guards the whole
        // behaviour but cannot by itself fail every time it should; the test above is the deterministic one.
        const int trials = 60;
        using var scope = _fixture.BeginScope();
        var locks = scope.Resolve<ICrossProcessLock>();
        var random = new Random(7);
        var outcomes = new Dictionary<string, int>();
        var leaked = 0;

        for (var trial = 0; trial < trials; trial++)
        {
            var key = NewKey();
            await using var holder = new NpgsqlConnection(Unpooled());
            await holder.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(holder, "SELECT pg_advisory_lock(@key)", key).ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource();
            var waiter = Attempt(locks, key, cancellation.Token);

            await WaitForAsync(async () => await WaitersAsync(key).ConfigureAwait(false) > 0, $"a backend to queue behind the lock on key {key}").ConfigureAwait(false);

            var cancelFirst = random.Next(2) == 0;
            var jitter = random.Next(0, 200);

            if (cancelFirst) cancellation.Cancel();
            for (var spin = 0; spin < jitter; spin++) Thread.SpinWait(50);
            await ExecuteAsync(holder, "SELECT pg_advisory_unlock(@key)", key).ConfigureAwait(false);
            if (!cancelFirst) cancellation.Cancel();

            var (handle, outcome) = await waiter.ConfigureAwait(false);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

            if (handle != null) await handle.DisposeAsync().ConfigureAwait(false);

            if (!await ReleasedSoonAsync(key).ConfigureAwait(false)) leaked++;
        }

        _output.WriteLine($"{trials} trials: {string.Join(", ", outcomes.Select(o => $"{o.Key}={o.Value}"))}; leaked={leaked}");
        leaked.ShouldBe(0, "a cancelled acquire left its lock held by a session nobody owns — the next refresh of that credential would queue behind it");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private static async Task<(IAsyncDisposable? Handle, string Outcome)> Attempt(ICrossProcessLock locks, long key, CancellationToken cancellationToken)
    {
        try
        {
            return (await locks.AcquireAsync(key, cancellationToken).ConfigureAwait(false), "acquired");
        }
        catch (OperationCanceledException)
        {
            return (null, "cancelled");
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name);
        }
    }

    private static long NewKey() => Random.Shared.NextInt64(1L << 40, long.MaxValue);

    private CodeSpaceConnectionString ConnectionStringWith(string extraOptions)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CodeSpaceStore:ConnectionString"] = _fixture.ConnectionString + extraOptions }).Build();

        return new CodeSpaceConnectionString(configuration);
    }

    private string Unpooled() => new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Pooling = false }.ConnectionString;

    /// <summary>pg_locks shows a bigint advisory key as its high half in classid and its low half in objid, with objsubid 1.</summary>
    private const string AdvisoryKeyPredicate = "locktype = 'advisory' AND objsubid = 1 AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) AND classid::bigint = @high AND objid::bigint = @low";

    private async Task<int> HolderPidAsync(long key) => Convert.ToInt32(await ScalarAsync($"SELECT pid FROM pg_locks WHERE granted AND {AdvisoryKeyPredicate}", key).ConfigureAwait(false));

    private async Task<bool> IsHeldAsync(long key) => Convert.ToInt64(await ScalarAsync($"SELECT count(*) FROM pg_locks WHERE granted AND {AdvisoryKeyPredicate}", key).ConfigureAwait(false)) > 0;

    private async Task<long> WaitersAsync(long key) => Convert.ToInt64(await ScalarAsync($"SELECT count(*) FROM pg_locks WHERE NOT granted AND {AdvisoryKeyPredicate}", key).ConfigureAwait(false));

    private async Task<bool> BackendExistsAsync(int pid)
    {
        await using var conn = new NpgsqlConnection(Unpooled());
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = @pid", conn);
        cmd.Parameters.AddWithValue("pid", pid);

        return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
    }

    /// <summary>A lock that was never released does not become free by waiting, so a short grace is all a late unlock needs.</summary>
    private async Task<bool> ReleasedSoonAsync(long key)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (await IsHeldAsync(key).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow > deadline) return false;

            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<object?> ScalarAsync(string sql, long key)
    {
        await using var conn = new NpgsqlConnection(Unpooled());
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("high", (long)(uint)(key >> 32));
        cmd.Parameters.AddWithValue("low", (long)(uint)key);

        return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, long key)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string signal)
    {
        var watch = Stopwatch.StartNew();

        while (!await condition().ConfigureAwait(false))
        {
            if (watch.Elapsed > Patience) throw new TimeoutException($"Waited {Patience.TotalSeconds}s for {signal}");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
