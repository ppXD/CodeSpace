using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Health;

/// <summary>
/// Takes one advisory-lock key on a session, tries it from a second, and reports whether the second was refused.
///
/// <para>OAuth token refresh holds <c>pg_advisory_lock</c> per credential so that two pods do not both spend the one
/// refresh token. The lock is only exclusive when each connection IS a server session. Behind a transaction-mode pooler
/// (PgBouncer), RDS Proxy or a read-write splitting proxy two connections can share one — where the lock is re-entrant —
/// or land on different ones, and refresh stops being serialized without any error. The compare-and-set write and the
/// re-read after the lock keep the stored tokens correct; what is lost is that a loser can still call the provider with a
/// token the winner has already spent. This test turns that from an inference into a line in the log and a field in
/// <c>/health</c>.</para>
///
/// <para>Two observations convict a pooler, and either one is certain. The contender taking the held key means two
/// connections shared a server session. The holder's own unlock being told it never held the key means one connection's
/// statements ran on different server sessions — the lock went to one, the unlock to another. The contender tries more than
/// once because a pooler that picks a server session per statement lands it on the holder's only some of the time; the
/// unlock's answer is read because it catches that same pooler on the one statement the contender cannot be fooled about.
/// A test that passes still cannot prove the lock is exclusive — it fails to find a pooler — and says so.</para>
///
/// <para>It never refuses anything: a lock that is not exclusive is logged as an Error and reported as Degraded, and the
/// process carries on. The finding is kept for <see cref="VerdictLifetime"/> and re-taken on the next ask after that, so
/// an anonymous endpoint reading it cannot make the database open connections per request, and a failover that changes the
/// topology is noticed.</para>
/// </summary>
public sealed class RefreshLockSelfTest : IRefreshLockSelfTest, ISingletonDependency
{
    /// <summary>How long a finding is reused before the next ask runs the test again.</summary>
    public static readonly TimeSpan VerdictLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The bound on one run: connecting two sessions to an unreachable server must not hold a health request, or the boot path, for the driver's own minutes.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many times the second session tries the held key before the lock is believed exclusive.</summary>
    public const int ContenderAttempts = 5;

    /// <summary>How many times an unlock that missed is asked again, hoping to land where the lock is, before the test gives up on clearing it.</summary>
    internal const int ReleaseAttempts = 5;

    private readonly IAdvisoryLockSessions _sessions;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshLockSelfTest> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private RefreshLockVerdict? _latest;
    private RefreshLockOutcome? _reported;

    public RefreshLockSelfTest(IAdvisoryLockSessions sessions, TimeProvider clock, ILogger<RefreshLockSelfTest> logger)
    {
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RefreshLockVerdict> VerdictAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } cached) return cached;

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Fresh() is { } raced) return raced;

            var verdict = await RunAsync(cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _latest, verdict);
            Report(verdict);

            return verdict;
        }
        finally
        {
            _running.Release();
        }
    }

    private RefreshLockVerdict? Fresh() => Volatile.Read(ref _latest) is { } latest && _clock.GetUtcNow() - latest.ObservedAt < VerdictLifetime ? latest : null;

    /// <summary>One run, bounded by <see cref="ProbeTimeout"/>. A failure to run is a finding of its own, not an exception: the caller is the boot path or a health request.</summary>
    private async Task<RefreshLockVerdict> RunAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await TakeTheKeyTwiceAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Observe(RefreshLockOutcome.Inconclusive, $"The self-test did not finish within {ProbeTimeout.TotalSeconds:0}s, so whether pg_advisory_lock is exclusive across connections is unknown.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The refresh-lock self-test could not run; whether pg_advisory_lock is exclusive across connections is unknown");

            return Observe(RefreshLockOutcome.Inconclusive, $"The self-test could not run ({ex.GetType().Name}), so whether pg_advisory_lock is exclusive across connections is unknown.");
        }
    }

    /// <summary>A fresh random key, so a second pod running this at the same moment cannot collide with it and no credential's refresh lock is touched.</summary>
    private async Task<RefreshLockVerdict> TakeTheKeyTwiceAsync(CancellationToken cancellationToken)
    {
        var key = Random.Shared.NextInt64();

        await using var holder = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var contender = await _sessions.OpenAsync(cancellationToken).ConfigureAwait(false);

        await holder.LockAsync(key, cancellationToken).ConfigureAwait(false);

        bool contenderTookTheKey;
        bool holderUnlocked;

        try
        {
            contenderTookTheKey = await ContendAsync(contender, key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            holderUnlocked = await ReleaseAsync(holder, key).ConfigureAwait(false);
        }

        return Conclude(contenderTookTheKey, holderUnlocked);
    }

    /// <summary>True when the contender was ever granted the held key — which it releases at once; false when every try was refused.</summary>
    private static async Task<bool> ContendAsync(IAdvisoryLockSession contender, long key, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ContenderAttempts; attempt++)
        {
            if (!await contender.TryLockAsync(key, cancellationToken).ConfigureAwait(false)) continue;

            await ReleaseAsync(contender, key).ConfigureAwait(false);

            return true;
        }

        return false;
    }

    private RefreshLockVerdict Conclude(bool contenderTookTheKey, bool holderUnlocked)
    {
        if (contenderTookTheKey) return Observe(RefreshLockOutcome.NotExclusive, NotExclusiveDetail);

        if (!holderUnlocked) return Observe(RefreshLockOutcome.NotExclusive, UnlockMissedDetail);

        return Observe(RefreshLockOutcome.Exclusive, ExclusiveDetail);
    }

    /// <summary>
    /// Lets go of <paramref name="key"/> and returns false when the server's FIRST answer was that this session did not hold it —
    /// the unlock ran on a server session that never took the lock, which leaves the lock where it was. It then asks again, a
    /// bounded number of times, hoping to land where the lock is, so a pooler does not accumulate a leaked key per test. An
    /// unlock that throws says nothing about the lock (true): the session is closed next, which releases it on a direct connection.
    /// </summary>
    private static async Task<bool> ReleaseAsync(IAdvisoryLockSession session, long key)
    {
        var missed = false;

        for (var attempt = 1; attempt <= ReleaseAttempts; attempt++)
        {
            try
            {
                if (await session.UnlockAsync(key, CancellationToken.None).ConfigureAwait(false)) return !missed;
            }
            catch
            {
                return !missed;
            }

            missed = true;
        }

        return false;
    }

    private RefreshLockVerdict Observe(RefreshLockOutcome outcome, string detail) => new(outcome, detail, _clock.GetUtcNow());

    /// <summary>Logged on a change of finding only: a re-test every few minutes must not repeat the same Error.</summary>
    private void Report(RefreshLockVerdict verdict)
    {
        var previous = _reported;

        if (previous == verdict.Outcome) return;

        _reported = verdict.Outcome;

        if (verdict.Outcome == RefreshLockOutcome.NotExclusive)
            _logger.LogError("Refresh-lock self-test failed: {Detail}", verdict.Detail);
        else if (verdict.Outcome == RefreshLockOutcome.Exclusive)
            _logger.LogInformation(previous == RefreshLockOutcome.NotExclusive ? "Refresh-lock self-test: no sign of multiplexed connections any more" : "Refresh-lock self-test passed: no sign that connections are multiplexed");
    }

    /// <summary>What a passing test says: what it saw and, as plainly, what it cannot see.</summary>
    internal const string ExclusiveDetail =
        "No sign that connections are multiplexed: a second session was refused the advisory lock the first one held, and the first released it as its own. " +
        "A pooler that picks a server session per statement can slip past this test, so it narrows the question and does not close it.";

    /// <summary>What an operator reads when the contender was GRANTED the held key: the symptom, the likely topology, what still holds, and the setting to change.</summary>
    internal const string NotExclusiveDetail =
        "A second Postgres session took the advisory lock the first one already held, so pg_advisory_lock does not serialize across this deployment's connections. " + WhatToCheck;

    /// <summary>What an operator reads when the holder's unlock was told it did not hold the key it had just taken.</summary>
    internal const string UnlockMissedDetail =
        "A Postgres session that had just taken an advisory lock was told, on unlocking it, that it did not hold it, so one connection's statements are served by different server sessions and pg_advisory_lock does not serialize across this deployment's connections. " + WhatToCheck;

    private const string WhatToCheck =
        "CodeSpaceStore:ConnectionString most likely points at a transaction-mode pooler (PgBouncer), RDS Proxy or a read-write splitting proxy. " +
        "OAuth token refresh then relies on its compare-and-set write and re-read alone: two refreshes of one credential can both call the provider. " +
        "Point it at the primary directly, or at a session-mode pool.";
}
