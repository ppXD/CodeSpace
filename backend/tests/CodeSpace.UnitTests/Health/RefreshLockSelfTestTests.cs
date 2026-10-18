using CodeSpace.Core.Services.Health;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// The startup self-test that takes one advisory-lock key on two connections and expects the second to be refused. OAuth
/// token refresh serializes on <c>pg_advisory_lock</c>; a transaction-mode pooler, RDS Proxy or a read-write splitting proxy
/// hands both connections the same server session, where the lock is re-entrant, and refresh quietly stops being
/// serialized. The test cannot stop that — the compare-and-set write and the re-read keep the stored tokens correct — it
/// names it. Driven here against a fake server that models exactly the property that matters: a lock is held by a SERVER
/// session, and a session may take its own lock again.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RefreshLockSelfTestTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_sessions_cannot_both_hold_the_key_so_the_lock_is_exclusive()
    {
        var server = new FakeServer();

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Exclusive);
        verdict.ObservedAt.ShouldBe(Start);
        server.Opened.ShouldBe(2, "one holder, one contender");
        server.ContenderAttempts.ShouldBe(RefreshLockSelfTest.ContenderAttempts, "an exclusive lock is only believed after the contender has tried more than once");
        server.AssertNothingLeftHeldAndEverythingClosed();
    }

    [Fact]
    public async Task A_second_session_that_takes_the_held_key_means_the_lock_is_not_exclusive()
    {
        var server = new FakeServer { EveryClientIsOneServerSession = true };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive);
        verdict.Detail.ShouldContain("pg_advisory_lock", customMessage: "it names the lock that failed");
        verdict.Detail.ShouldContain("PgBouncer", customMessage: "and the topologies that cause it");
        verdict.Detail.ShouldContain("RDS Proxy");
        verdict.Detail.ShouldContain("compare-and-set", customMessage: "and what still holds when the lock does not");
        verdict.Detail.ShouldContain("CodeSpaceStore:ConnectionString", customMessage: "and which setting to change");
        server.ContenderAttempts.ShouldBe(1, "the first answer that breaks the property settles it");
        server.AssertNothingLeftHeldAndEverythingClosed();
    }

    [Fact]
    public async Task A_pooler_that_only_sometimes_lands_both_sessions_on_one_server_is_still_caught()
    {
        // A pool of server sessions hands the contender a different one first, then the holder's. Trying once would call that lock exclusive.
        var server = new FakeServer { ContenderLandsOnTheHoldersSessionAtAttempt = 4 };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive);
        server.ContenderAttempts.ShouldBe(4);
        server.AssertNothingLeftHeldAndEverythingClosed();
    }

    [Fact]
    public async Task A_holder_told_it_never_held_the_lock_it_took_means_statements_are_multiplexed_even_though_the_contender_was_refused()
    {
        // A transaction-mode pooler serves each statement from some server session: the lock lands on one, the unlock on another.
        // The contender can miss the holder's server session every time; the unlock's answer cannot be talked away.
        var server = new FakeServer { UnlockMissesBeforeLanding = 1 };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive, "the holder's own unlock said it did not hold the key");
        verdict.Detail.ShouldContain("did not hold", customMessage: "it names what was observed");
        verdict.Detail.ShouldContain("PgBouncer", customMessage: "and the topologies that cause it");
        verdict.Detail.ShouldContain("compare-and-set");
        verdict.Detail.ShouldContain("CodeSpaceStore:ConnectionString");
        server.ContenderAttempts.ShouldBe(RefreshLockSelfTest.ContenderAttempts, "the contender was refused every time, and that alone proves nothing");
        server.AssertNothingLeftHeldAndEverythingClosed();
    }

    [Fact]
    public async Task An_unlock_that_misses_is_retried_to_clear_the_lock_it_left_but_only_a_bounded_number_of_times()
    {
        var server = new FakeServer { UnlockMissesBeforeLanding = 100 };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.NotExclusive);
        server.UnlockCalls.ShouldBe(RefreshLockSelfTest.ReleaseAttempts, "a pooler that never lands the unlock where the lock is must not keep the boot path busy");
    }

    [Fact]
    public async Task An_unlock_that_throws_is_no_evidence_about_the_lock_either_way()
    {
        var server = new FakeServer { UnlockFails = new InvalidOperationException("connection reset by 10.20.30.40") };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Exclusive, "the contender was refused and the unlock said nothing: not a finding against the lock");
        verdict.Detail.ShouldNotContain("10.20.30.40");
    }

    [Fact]
    public async Task A_lock_that_passes_says_it_found_no_sign_of_multiplexing_not_that_it_proved_exclusivity()
    {
        var verdict = await NewTest(new FakeServer()).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Exclusive);
        verdict.Detail.ShouldContain("no sign", customMessage: "a pass fails to find a pooler; it cannot prove there is none");
        verdict.Detail.ShouldNotContain("is exclusive across connections");
    }

    [Fact]
    public async Task A_test_that_cannot_run_is_inconclusive_and_does_not_echo_the_exception_message()
    {
        var server = new FakeServer { OpenFails = new InvalidOperationException("Failed to connect to 10.20.30.40:5432") };

        var verdict = await NewTest(server).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Inconclusive);
        verdict.Detail.ShouldContain(nameof(InvalidOperationException), customMessage: "the type says what kind of failure it was");
        verdict.Detail.ShouldNotContain("10.20.30.40", customMessage: "/health is anonymous: an address from an exception message must stay in the log");
        verdict.Detail.ShouldContain("unknown", customMessage: "and that nothing is known about the lock");
    }

    [Fact]
    public async Task A_test_that_hangs_ends_as_inconclusive_when_the_timeout_passes()
    {
        var clock = new FakeTimeProvider(Start);
        var opening = new TaskCompletionSource();
        var server = new FakeServer { OnOpen = async cancellationToken => { opening.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); } };
        var test = NewTest(server, clock);

        var pending = test.VerdictAsync(CancellationToken.None);
        await opening.Task;
        clock.Advance(RefreshLockSelfTest.ProbeTimeout);

        var verdict = await pending;

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Inconclusive);
        verdict.Detail.ShouldContain("did not finish");
    }

    [Fact]
    public async Task A_caller_that_cancels_gets_the_cancellation_and_nothing_is_remembered()
    {
        var server = new FakeServer { OnOpen = async cancellationToken => await Task.Delay(Timeout.Infinite, cancellationToken) };
        var test = NewTest(server);
        using var cancel = new CancellationTokenSource();

        var pending = test.VerdictAsync(cancel.Token);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);

        server.OnOpen = null;
        (await test.VerdictAsync(CancellationToken.None)).Outcome.ShouldBe(RefreshLockOutcome.Exclusive, "a cancelled caller must not leave a stale verdict behind");
    }

    [Fact]
    public async Task The_verdict_is_reused_until_it_is_older_than_its_lifetime()
    {
        var clock = new FakeTimeProvider(Start);
        var server = new FakeServer();
        var test = NewTest(server, clock);

        var first = await test.VerdictAsync(CancellationToken.None);
        clock.Advance(RefreshLockSelfTest.VerdictLifetime - TimeSpan.FromSeconds(1));
        var reused = await test.VerdictAsync(CancellationToken.None);

        reused.ShouldBeSameAs(first);
        server.Opened.ShouldBe(2, "an anonymous endpoint must not open two connections per request");

        clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = await test.VerdictAsync(CancellationToken.None);

        fresh.ShouldNotBeSameAs(first);
        fresh.ObservedAt.ShouldBe(Start + RefreshLockSelfTest.VerdictLifetime);
        server.Opened.ShouldBe(4);
    }

    [Fact]
    public async Task Askers_that_arrive_together_share_one_test()
    {
        var release = new TaskCompletionSource();
        var server = new FakeServer { OnOpen = _ => release.Task };
        var test = NewTest(server);

        var asks = Enumerable.Range(0, 5).Select(_ => test.VerdictAsync(CancellationToken.None)).ToList();
        release.SetResult();
        var verdicts = await Task.WhenAll(asks);

        verdicts.Distinct().Count().ShouldBe(1);
        server.Opened.ShouldBe(2);
    }

    [Fact]
    public async Task A_lock_that_is_not_exclusive_is_logged_as_an_Error_once_and_not_again_while_it_stays_so()
    {
        var clock = new FakeTimeProvider(Start);
        var logger = new CapturingLogger<RefreshLockSelfTest>();
        var test = NewTest(new FakeServer { EveryClientIsOneServerSession = true }, clock, logger);

        await test.VerdictAsync(CancellationToken.None);
        clock.Advance(RefreshLockSelfTest.VerdictLifetime);
        await test.VerdictAsync(CancellationToken.None);

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();

        errors.ShouldHaveSingleItem("a re-test every few minutes must not repeat the same Error");
        errors[0].Message.ShouldContain("PgBouncer");
    }

    [Fact]
    public async Task A_lock_that_turns_exclusive_after_not_being_so_says_so_once()
    {
        var clock = new FakeTimeProvider(Start);
        var logger = new CapturingLogger<RefreshLockSelfTest>();
        var server = new FakeServer { EveryClientIsOneServerSession = true };
        var test = NewTest(server, clock, logger);

        await test.VerdictAsync(CancellationToken.None);
        server.EveryClientIsOneServerSession = false;
        clock.Advance(RefreshLockSelfTest.VerdictLifetime);
        await test.VerdictAsync(CancellationToken.None);

        logger.Entries.Where(e => e.Level == LogLevel.Information).ShouldHaveSingleItem().Message.ShouldContain("no sign");
    }

    [Fact]
    public async Task A_test_that_cannot_run_is_a_Warning_with_the_exception_never_an_Error()
    {
        var logger = new CapturingLogger<RefreshLockSelfTest>();
        var test = NewTest(new FakeServer { OpenFails = new InvalidOperationException("no route") }, new FakeTimeProvider(Start), logger);

        await test.VerdictAsync(CancellationToken.None);

        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    private static RefreshLockSelfTest NewTest(FakeServer server, FakeTimeProvider? clock = null, ILogger<RefreshLockSelfTest>? logger = null) =>
        new(server, clock ?? new FakeTimeProvider(Start), logger ?? new CapturingLogger<RefreshLockSelfTest>());

    /// <summary>
    /// A Postgres server's advisory locks, as far as the self-test can tell. A lock is held by a server SESSION, a session may
    /// take a lock it already holds (re-entrant), and a different session is refused. A client normally has a session of its
    /// own; behind a transaction-mode pooler every client lands on one.
    /// </summary>
    private sealed class FakeServer : IAdvisoryLockSessions
    {
        private readonly Dictionary<long, (int Session, int Depth)> _held = new();
        private int _nextSession;
        private int _opened;
        private int _closed;

        public bool EveryClientIsOneServerSession { get; set; }

        /// <summary>Models a session pool: the contender's try-locks run on another server session until this attempt, and on the holder's from then on. Zero = never.</summary>
        public int ContenderLandsOnTheHoldersSessionAtAttempt { get; init; }

        /// <summary>Models a statement-level pooler: this many unlocks run on a server session that does not hold the key (answering false, changing nothing) before one lands on the holder's.</summary>
        public int UnlockMissesBeforeLanding { get; set; }

        /// <summary>Every unlock throws this — a connection that dropped after the contender was refused.</summary>
        public Exception? UnlockFails { get; init; }

        public Exception? OpenFails { get; init; }

        public Func<CancellationToken, Task>? OnOpen { get; set; }

        public int Opened => _opened;

        public int ContenderAttempts { get; private set; }

        public int UnlockCalls { get; private set; }

        public async Task<IAdvisoryLockSession> OpenAsync(CancellationToken cancellationToken)
        {
            if (OnOpen is { } onOpen) await onOpen(cancellationToken);

            if (OpenFails is { } failure) throw failure;

            Interlocked.Increment(ref _opened);

            return new Session(this, EveryClientIsOneServerSession ? 1 : Interlocked.Increment(ref _nextSession), isContender: _opened % 2 == 0);
        }

        public void AssertNothingLeftHeldAndEverythingClosed()
        {
            _held.ShouldBeEmpty("the test must never leave a key held — a real one would hold it until the connection died");
            _closed.ShouldBe(_opened, "every session the test opened is disposed");
        }

        private sealed class Session(FakeServer server, int serverSession, bool isContender) : IAdvisoryLockSession
        {
            public Task LockAsync(long key, CancellationToken cancellationToken)
            {
                if (server._held.TryGetValue(key, out var held) && held.Session != serverSession) throw new InvalidOperationException("a blocking lock on a held key would hang the test: the holder must lock a fresh key");

                server._held[key] = (serverSession, server._held.TryGetValue(key, out var again) ? again.Depth + 1 : 1);

                return Task.CompletedTask;
            }

            public Task<bool> TryLockAsync(long key, CancellationToken cancellationToken)
            {
                if (isContender) server.ContenderAttempts++;

                var effective = server.ContenderLandsOnTheHoldersSessionAtAttempt is > 0 && isContender
                    ? (server.ContenderAttempts >= server.ContenderLandsOnTheHoldersSessionAtAttempt ? 1 : 99)
                    : serverSession;

                if (server._held.TryGetValue(key, out var held) && held.Session != effective) return Task.FromResult(false);

                server._held[key] = (effective, held.Depth + 1);

                return Task.FromResult(true);
            }

            public Task<bool> UnlockAsync(long key, CancellationToken cancellationToken)
            {
                server.UnlockCalls++;

                if (server.UnlockFails is { } failure) throw failure;

                if (server.UnlockMissesBeforeLanding > 0)
                {
                    server.UnlockMissesBeforeLanding--;

                    return Task.FromResult(false);
                }

                if (!server._held.TryGetValue(key, out var held)) return Task.FromResult(false);

                if (held.Depth <= 1) server._held.Remove(key);
                else server._held[key] = (held.Session, held.Depth - 1);

                return Task.FromResult(true);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref server._closed);

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
