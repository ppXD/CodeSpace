using CodeSpace.Core.Services.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// One pass of a recurring sweep over a backlog that must not starve: it walks the backlog in pages behind a cursor, so a
/// candidate that cannot succeed costs one attempt and no place in the batch; it is bounded in attempts and in candidates
/// examined as well as in results; and a candidate that keeps failing is set aside rather than attempted every tick.
/// </summary>
[Trait("Category", "Unit")]
public class SweepPassTests
{
    private const string Sweep = "test-sweep";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly SweepFailureBackoff _backoff;
    private readonly RecordingLogger _logger = new();
    private readonly Dictionary<int, int> _attempts = new();

    public SweepPassTests() { _backoff = new SweepFailureBackoff(_clock); }

    [Fact]
    public async Task Candidates_that_always_fail_do_not_take_the_places_of_the_ones_behind_them()
    {
        var backlog = Backlog(5);
        var failing = new HashSet<int> { 0, 1 };

        var done = await RunAsync(backlog, batchSize: 2, failing);

        done.ShouldBe(2);
        // The two that fail were passed over, and the pass reached the two behind them.
        Attempted().ShouldBe(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public async Task A_pass_stops_when_its_batch_is_done_however_much_is_left()
    {
        var done = await RunAsync(Backlog(10), batchSize: 3, failing: new HashSet<int>());

        done.ShouldBe(3);
        Attempted().ShouldBe(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task A_pass_ends_with_the_backlog()
    {
        var done = await RunAsync(Backlog(3), batchSize: 50, failing: new HashSet<int>());

        done.ShouldBe(3);
        Attempted().ShouldBe(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task A_candidate_with_nothing_to_do_is_not_counted_and_is_not_a_failure()
    {
        var backlog = Backlog(3);

        var done = await new SweepPass<Candidate>(Sweep, _backoff, _logger).RunAsync(50, Loader(backlog), (c, _) => { Count(c); return Task.FromResult(c.Index != 1); }, CancellationToken.None);

        done.ShouldBe(2, "the one that found nothing to do is not retired work");
        _logger.Warnings.ShouldBeEmpty("and is not logged as a failure");
    }

    [Fact]
    public async Task A_pass_attempts_a_bounded_number_however_many_candidates_fail()
    {
        var backlog = Backlog(100);

        var done = await RunAsync(backlog, batchSize: 1, failing: Enumerable.Range(0, 100).ToHashSet());

        done.ShouldBe(0);
        _attempts.Values.Sum().ShouldBe(SweepPassBounds.AttemptsPerBatchSlot, "attempts are bounded by the batch, not only the successes");
    }

    [Fact]
    public async Task A_pass_examines_a_bounded_number_even_when_every_candidate_is_set_aside()
    {
        var backlog = Backlog(1000);

        foreach (var candidate in backlog) SetAside(candidate);

        var done = await RunAsync(backlog, batchSize: 1, failing: new HashSet<int>());

        done.ShouldBe(0);
        _attempts.ShouldBeEmpty("a candidate that is set aside is not attempted");
        _logger.Reads.ShouldBeLessThanOrEqualTo(SweepPassBounds.ExaminedPerBatchSlot + 1, "the walk through set-aside candidates ends with the examined bound");
    }

    [Fact]
    public async Task A_loader_that_ignores_its_cursor_ends_the_pass_instead_of_hanging_it()
    {
        var page = Backlog(2);
        var reads = 0;

        LoadSweepPage<Candidate> ignoresItsCursor = (_, _, _) =>
        {
            if (++reads > 100) throw new ShouldAssertException("the pass kept reading the same page");

            return Task.FromResult(page.ToList());
        };

        var done = await new SweepPass<Candidate>(Sweep, _backoff, _logger).RunAsync(2, ignoresItsCursor, (c, _) => { Count(c); return Task.FromResult(false); }, CancellationToken.None);

        done.ShouldBe(0);
        reads.ShouldBe(2, "the second page ended where the first did, so the pass stopped");
        _logger.Errors.ShouldNotBeEmpty("and said why");
    }

    [Fact]
    public async Task A_candidate_that_keeps_failing_is_set_aside_logged_once_and_attempted_again_when_the_window_ends()
    {
        var backlog = Backlog(2);
        var failing = new HashSet<int> { 0 };

        for (var pass = 0; pass < SweepFailureBackoff.FailuresBeforeBackoff; pass++) await RunAsync(backlog, batchSize: 50, failing);

        _attempts[0].ShouldBe(SweepFailureBackoff.FailuresBeforeBackoff);
        _logger.Warnings.Count(w => w.Contains("set aside")).ShouldBe(1, "said once, by the failure that set it aside");

        await RunAsync(backlog, batchSize: 50, failing);
        await RunAsync(backlog, batchSize: 50, failing);

        _attempts[0].ShouldBe(SweepFailureBackoff.FailuresBeforeBackoff, "not attempted while set aside");
        _logger.Warnings.Count.ShouldBe(SweepFailureBackoff.FailuresBeforeBackoff, "and not logged while set aside");
        _attempts[1].ShouldBe(SweepFailureBackoff.FailuresBeforeBackoff + 2, "the candidate beside it is attempted every pass");

        _clock.Advance(SweepFailureBackoff.BackoffWindow + TimeSpan.FromSeconds(1));
        await RunAsync(backlog, batchSize: 50, failing);

        _attempts[0].ShouldBe(SweepFailureBackoff.FailuresBeforeBackoff + 1, "attempted once when the window ends");
    }

    [Fact]
    public async Task A_candidate_that_succeeds_between_failures_is_never_set_aside()
    {
        // Failures in a row, not failures in total: a candidate that fails twice, is repaired, and fails twice again has not
        // failed three passes running.
        var backlog = Backlog(1);
        var outcomes = new Queue<bool>(Enumerable.Repeat(false, SweepFailureBackoff.FailuresBeforeBackoff - 1).Append(true).Concat(Enumerable.Repeat(false, SweepFailureBackoff.FailuresBeforeBackoff - 1)));
        var passes = outcomes.Count;

        for (var pass = 0; pass < passes; pass++)
        {
            var succeeds = outcomes.Dequeue();

            await new SweepPass<Candidate>(Sweep, _backoff, _logger).RunAsync(50, Loader(backlog), (c, _) => { Count(c); return succeeds ? Task.FromResult(true) : throw new InvalidOperationException("fails"); }, CancellationToken.None);
        }

        _attempts[0].ShouldBe(passes, "attempted every pass, never set aside");
        _backoff.IsBackedOff(Sweep, backlog[0].Id).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancellation_is_not_a_failure()
    {
        var backlog = Backlog(2);

        await Should.ThrowAsync<OperationCanceledException>(() => new SweepPass<Candidate>(Sweep, _backoff, _logger).RunAsync(50, Loader(backlog), (_, _) => throw new OperationCanceledException(), CancellationToken.None));

        _backoff.IsBackedOff(Sweep, backlog[0].Id).ShouldBeFalse();
        _logger.Warnings.ShouldBeEmpty();
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────

    private sealed record Candidate(int Index) : ISweepCandidate
    {
        public Guid Id { get; } = new(Index + 1, 0, 0, new byte[8]);
    }

    private static List<Candidate> Backlog(int count) => Enumerable.Range(0, count).Select(i => new Candidate(i)).ToList();

    private void SetAside(Candidate candidate)
    {
        for (var failure = 0; failure < SweepFailureBackoff.FailuresBeforeBackoff; failure++) _backoff.RecordFailure(Sweep, candidate.Id);
    }

    private async Task<int> RunAsync(List<Candidate> backlog, int batchSize, HashSet<int> failing) =>
        await new SweepPass<Candidate>(Sweep, _backoff, _logger).RunAsync(batchSize, Loader(backlog), (c, _) => { Count(c); return failing.Contains(c.Index) ? throw new InvalidOperationException($"candidate {c.Index} cannot be done") : Task.FromResult(true); }, CancellationToken.None);

    /// <summary>The loader a service writes: the candidates past the cursor, in order, one page at a time.</summary>
    private LoadSweepPage<Candidate> Loader(List<Candidate> backlog) => (after, take, _) =>
    {
        _logger.Reads++;

        var past = after is Guid cursor ? backlog.SkipWhile(c => c.Id != cursor).Skip(1) : backlog;

        return Task.FromResult(past.Take(take).ToList());
    };

    private void Count(Candidate candidate) => _attempts[candidate.Index] = _attempts.GetValueOrDefault(candidate.Index) + 1;

    private List<int> Attempted() => _attempts.Keys.OrderBy(i => i).ToList();

    private sealed class RecordingLogger : ILogger
    {
        public int Reads { get; set; }
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
