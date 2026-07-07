using System.Diagnostics;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the scoped, bounded retry the <see cref="RetryingSupervisorDeciderDecorator"/> puts around the supervisor
/// brain call, driven against a programmable fake <see cref="ISupervisorDecider"/> (the honest seam — only the inner
/// decider is replaced). Pins the EXACT scope: a transient/rate-limited infra fault AND the decorator's own per-attempt
/// timeout self-heal; a deterministic outcome (a returned stop / an AuthFailed / a BadRequest) is NEVER retried; the
/// run's own cancellation is never retried and never masked; an exhausted bound re-throws a transient so the engine
/// terminalizes exactly as before.
/// </summary>
[Trait("Category", "Unit")]
public class RetryingSupervisorDeciderDecoratorTests
{
    private static readonly SupervisorTurnContext Ctx = new() { Goal = "ship the feature", TurnNumber = 2 };

    private static readonly SupervisorDecision Stop = new() { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" };

    private static SupervisorDecisionRetryOptions Options(int maxAttempts = 3, int timeoutMs = 5000, int baseBackoffMs = 0) =>
        new() { MaxAttempts = maxAttempts, PerCallTimeout = TimeSpan.FromMilliseconds(timeoutMs), BaseBackoff = TimeSpan.FromMilliseconds(baseBackoffMs) };

    private static RetryingSupervisorDeciderDecorator Decorator(ScriptedInner inner, SupervisorDecisionRetryOptions? options = null) =>
        new(inner, options ?? Options(), NullLogger<RetryingSupervisorDeciderDecorator>.Instance);

    private static LlmApiException Fault(LlmErrorCategory category, TimeSpan? retryAfter = null) => new("test-gateway", 500, category, "boom", retryAfter);

    // ── Transient infra faults self-heal ─────────────────────────────────────────────

    [Theory]
    [InlineData(LlmErrorCategory.Transient)]
    [InlineData(LlmErrorCategory.RateLimited)]
    public async Task A_retryable_infra_fault_is_retried_then_recovers(LlmErrorCategory category)
    {
        var inner = new ScriptedInner(_ => throw Fault(category), _ => Task.FromResult(Stop));

        var decision = await Decorator(inner).DecideAsync(Ctx, CancellationToken.None);

        decision.ShouldBe(Stop);
        inner.Calls.ShouldBe(2, "the first transient attempt is retried, the second succeeds");
    }

    [Fact]
    public async Task An_exhausted_transient_re_throws_so_the_engine_terminalizes()
    {
        var inner = new ScriptedInner(_ => throw Fault(LlmErrorCategory.Transient), _ => throw Fault(LlmErrorCategory.Transient), _ => throw Fault(LlmErrorCategory.Transient));

        var ex = await Should.ThrowAsync<LlmApiException>(() => Decorator(inner, Options(maxAttempts: 3)).DecideAsync(Ctx, CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Transient);
        inner.Calls.ShouldBe(3, "all three attempts run, then the last transient propagates unchanged");
    }

    // ── The decorator's OWN per-attempt timeout is a retryable transient ──────────────

    [Fact]
    public async Task A_per_attempt_timeout_is_retried_then_recovers()
    {
        var inner = new ScriptedInner(Hang, _ => Task.FromResult(Stop));

        var decision = await Decorator(inner, Options(maxAttempts: 2, timeoutMs: 60)).DecideAsync(Ctx, CancellationToken.None);

        decision.ShouldBe(Stop);
        inner.Calls.ShouldBe(2, "the first attempt times out (the gateway hung), the retry returns");
    }

    [Fact]
    public async Task An_exhausted_timeout_surfaces_as_a_transient_LlmApiException()
    {
        var inner = new ScriptedInner(Hang, Hang);

        var ex = await Should.ThrowAsync<LlmApiException>(() => Decorator(inner, Options(maxAttempts: 2, timeoutMs: 60)).DecideAsync(Ctx, CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Transient);
        ex.Message.ShouldContain("did not return");
        inner.Calls.ShouldBe(2);
    }

    // ── Deterministic outcomes are NEVER retried ─────────────────────────────────────

    [Fact]
    public async Task A_returned_stop_passes_straight_through_unretried()
    {
        // A model capability-miss is swallowed to a clean stop INSIDE the decider — it returns as a DECISION, so the
        // decorator must hand it back on the first attempt, never re-ask the brain.
        var inner = new ScriptedInner(_ => Task.FromResult(Stop));

        var decision = await Decorator(inner).DecideAsync(Ctx, CancellationToken.None);

        decision.ShouldBe(Stop);
        inner.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(LlmErrorCategory.AuthFailed)]
    [InlineData(LlmErrorCategory.BadRequest)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded)]
    public async Task A_non_retryable_fault_propagates_on_the_first_attempt(LlmErrorCategory category)
    {
        var inner = new ScriptedInner(_ => throw Fault(category));

        var ex = await Should.ThrowAsync<LlmApiException>(() => Decorator(inner).DecideAsync(Ctx, CancellationToken.None));

        ex.Category.ShouldBe(category);
        inner.Calls.ShouldBe(1, "a deterministic fault is never retried — re-asking would re-author the same bad outcome");
    }

    // ── The run's own cancellation is never retried and never masked ─────────────────

    [Fact]
    public async Task The_outer_run_cancellation_propagates_and_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var inner = new ScriptedInner(ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Stop); });

        await Should.ThrowAsync<OperationCanceledException>(() => Decorator(inner).DecideAsync(Ctx, cts.Token));

        inner.Calls.ShouldBe(1, "a cancelled run is not a transient — it is never retried, and the OCE is not masked as a transient");
    }

    // ── The disabled escape hatch ────────────────────────────────────────────────────

    [Fact]
    public async Task With_one_attempt_a_transient_propagates_immediately()
    {
        var inner = new ScriptedInner(_ => throw Fault(LlmErrorCategory.Transient));

        await Should.ThrowAsync<LlmApiException>(() => Decorator(inner, Options(maxAttempts: 1)).DecideAsync(Ctx, CancellationToken.None));

        inner.Calls.ShouldBe(1, "MaxAttempts = 1 disables the retry");
    }

    // ── Backoff: honor a provider Retry-After, and bail out of a long backoff on cancellation ─────

    [Fact]
    public async Task A_rate_limit_Retry_After_is_honored_over_the_base_backoff()
    {
        // The fault carries a 1ms Retry-After; the base backoff is 30s. If Retry-After is honored the retry returns
        // near-instantly — if it were ignored the test would wait out the 30s base backoff.
        var inner = new ScriptedInner(_ => throw Fault(LlmErrorCategory.RateLimited, TimeSpan.FromMilliseconds(1)), _ => Task.FromResult(Stop));

        var stopwatch = Stopwatch.StartNew();
        var decision = await Decorator(inner, Options(maxAttempts: 2, baseBackoffMs: 30_000)).DecideAsync(Ctx, CancellationToken.None);
        stopwatch.Stop();

        decision.ShouldBe(Stop);
        inner.Calls.ShouldBe(2);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the provider's 1ms Retry-After was honored, not the 30s base backoff");
    }

    [Fact]
    public async Task An_outer_cancellation_during_the_backoff_propagates_and_stops_the_retry()
    {
        using var cts = new CancellationTokenSource();

        // The first attempt faults transiently → the decorator enters a 30s base backoff; the run is cancelled mid-sleep.
        var inner = new ScriptedInner(_ => throw Fault(LlmErrorCategory.Transient), _ => Task.FromResult(Stop));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => Decorator(inner, Options(maxAttempts: 3, baseBackoffMs: 30_000)).DecideAsync(Ctx, cts.Token));
        stopwatch.Stop();

        inner.Calls.ShouldBe(1, "the run was cancelled mid-backoff — the retry never re-ran the inner decider");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the backoff was interrupted by the cancellation, not waited out");
    }

    // ── ComputeDelay: exponential growth, caps, jitter band — pure, no sleeps ─────────

    [Theory]
    [InlineData(1, 2)]   // attempt 1 → BaseBackoff × 2^0 = 2s
    [InlineData(2, 4)]   // attempt 2 → 4s
    [InlineData(3, 8)]   // attempt 3 → 8s
    [InlineData(4, 16)]  // attempt 4 → 16s
    public void The_backoff_grows_exponentially_within_the_jitter_band(int attempt, int expectedBaseSeconds)
    {
        var delay = RetryingSupervisorDeciderDecorator.ComputeDelay(new SupervisorDecisionRetryOptions { BaseBackoff = TimeSpan.FromSeconds(2) }, attempt, retryAfter: null);

        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(expectedBaseSeconds * 0.8), "jitter floor is −20%");
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(expectedBaseSeconds * 1.2), "jitter roof is +20%");
    }

    [Fact]
    public void A_deep_attempt_is_capped_at_the_backoff_ceiling()
    {
        // Attempt 10 would be 2 × 2^9 = 1024s uncapped — the ceiling holds it at 60s (±20% jitter).
        var delay = RetryingSupervisorDeciderDecorator.ComputeDelay(new SupervisorDecisionRetryOptions { BaseBackoff = TimeSpan.FromSeconds(2) }, attempt: 10, retryAfter: null);

        delay.ShouldBeLessThanOrEqualTo(SupervisorDecisionRetryOptions.BackoffCeiling * 1.2);
    }

    [Fact]
    public void A_reasonable_Retry_After_is_honored_verbatim_without_jitter()
    {
        var delay = RetryingSupervisorDeciderDecorator.ComputeDelay(new SupervisorDecisionRetryOptions(), attempt: 1, retryAfter: TimeSpan.FromSeconds(30));

        delay.ShouldBe(TimeSpan.FromSeconds(30), "the provider knows when it recovers — its hint wins over the exponential schedule");
    }

    [Fact]
    public void A_hostile_Retry_After_is_clamped_to_the_ceiling()
    {
        var delay = RetryingSupervisorDeciderDecorator.ComputeDelay(new SupervisorDecisionRetryOptions(), attempt: 1, retryAfter: TimeSpan.FromHours(2));

        delay.ShouldBe(SupervisorDecisionRetryOptions.RetryAfterCeiling, "a misconfigured gateway header must not pin a worker for hours");
    }

    [Fact]
    public void A_zero_base_backoff_computes_a_zero_delay()
    {
        // Tests (and operators who want immediate retries) set BaseBackoff = 0 — jitter must not turn 0 into a sleep.
        RetryingSupervisorDeciderDecorator.ComputeDelay(new SupervisorDecisionRetryOptions { BaseBackoff = TimeSpan.Zero }, attempt: 3, retryAfter: null).ShouldBe(TimeSpan.Zero);
    }

    private static async Task<SupervisorDecision> Hang(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        return Stop;
    }

    /// <summary>A fake inner decider that runs one scripted step per call (in order), tracking the call count.</summary>
    private sealed class ScriptedInner : ISupervisorDecider
    {
        private readonly Queue<Func<CancellationToken, Task<SupervisorDecision>>> _steps;

        public ScriptedInner(params Func<CancellationToken, Task<SupervisorDecision>>[] steps) => _steps = new Queue<Func<CancellationToken, Task<SupervisorDecision>>>(steps);

        public int Calls { get; private set; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Calls++;

            if (_steps.Count == 0) throw new InvalidOperationException("ScriptedInner ran out of steps");

            return _steps.Dequeue()(cancellationToken);
        }
    }
}
