using System.Net;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Resilience;

[Trait("Category", "Unit")]
public class ExternalCallResilienceTests
{
    private static readonly ProviderInstance Instance = new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Provider = ProviderKind.GitHub,
        DisplayName = "test",
        BaseUrl = "https://test.local"
    };

    // ── Public consts pinned ──
    // Operators read these from logs / dashboards. Pinned because rename = silent prod change.

    [Fact]
    public void MaxAttempts_constant_pinned() => ExternalCallResilience.MaxAttempts.ShouldBe(3);

    [Fact]
    public void BaseDelayMs_constant_pinned() => ExternalCallResilience.BaseDelayMs.ShouldBe(200);

    [Fact]
    public void TokensPerMinute_constant_pinned() => ExternalCallResilience.TokensPerMinute.ShouldBe(300);

    [Fact]
    public void QueueLimit_constant_pinned() => ExternalCallResilience.QueueLimit.ShouldBe(50);

    // ── Backoff ──

    [Theory]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(3, 800)]
    public void ComputeBackoff_doubles_per_attempt(int attempt, double expectedMs)
    {
        ExternalCallResilience.ComputeBackoff(attempt).TotalMilliseconds.ShouldBe(expectedMs);
    }

    // ── Transient detection ──

    [Fact]
    public void IsTransient_HttpRequestException_is_transient() => ExternalCallResilience.IsTransient(new HttpRequestException("network down")).ShouldBeTrue();

    [Fact]
    public void IsTransient_TaskCanceledException_is_transient() => ExternalCallResilience.IsTransient(new TaskCanceledException("timeout")).ShouldBeTrue();

    // NGitLab speaks HttpWebRequest, so its network blips are not HttpRequestException: a connection it never got an
    // answer on is a WebException without a response, and an answer cut short mid-body is an HttpIOException.

    [Theory]
    [InlineData(WebExceptionStatus.ConnectFailure)]
    [InlineData(WebExceptionStatus.Timeout)]
    [InlineData(WebExceptionStatus.ConnectionClosed)]
    public void IsTransient_WebException_without_a_response_is_transient(WebExceptionStatus status) => ExternalCallResilience.IsTransient(new WebException("no answer", status)).ShouldBeTrue();

    [Fact]
    public void IsTransient_HttpIOException_is_transient() => ExternalCallResilience.IsTransient(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")).ShouldBeTrue();

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public void IsTransient_5xx_status_is_transient(int statusCode) => ExternalCallResilience.IsTransient(new FakeSdkException(statusCode)).ShouldBeTrue();

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void IsTransient_4xx_status_is_not_transient(int statusCode) => ExternalCallResilience.IsTransient(new FakeSdkException(statusCode)).ShouldBeFalse();

    [Fact]
    public void IsTransient_arbitrary_exception_without_StatusCode_is_not_transient() => ExternalCallResilience.IsTransient(new InvalidOperationException("nope")).ShouldBeFalse();

    [Fact]
    public void IsTransient_HttpStatusCode_enum_property_is_supported() => ExternalCallResilience.IsTransient(new FakeSdkExceptionWithEnumStatusCode(HttpStatusCode.InternalServerError)).ShouldBeTrue();

    // ── Retry behaviour ──

    [Fact]
    public async Task ExecuteAsync_returns_immediately_when_operation_succeeds()
    {
        var policy = BuildPolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(Instance, "test", _ =>
        {
            calls++;
            return Task.FromResult(42);
        }, CancellationToken.None);

        result.ShouldBe(42);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_retries_on_transient_and_succeeds_on_second_attempt()
    {
        var policy = BuildPolicy();
        var calls = 0;

        var result = await policy.ExecuteAsync(Instance, "test", _ =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("flake");
            return Task.FromResult("ok");
        }, CancellationToken.None);

        result.ShouldBe("ok");
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_on_non_transient()
    {
        var policy = BuildPolicy();
        var calls = 0;

        var act = async () => await policy.ExecuteAsync<int>(Instance, "test", _ =>
        {
            calls++;
            throw new InvalidOperationException("not transient");
        }, CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_lets_a_plan_refusal_through_untouched()
    {
        // Already typed where it was raised: it names the plan the call needs and the way around it. Re-labelled as a bare
        // ProviderApiException(403) it would read as a credential problem and send the operator to re-scope a good token.
        var refusal = new ProviderPlanRequirementException(ProviderKind.GitLab, "group webhooks", "Premium", 403, "Upgrade the group, or stay on per-repository scope.", new InvalidOperationException("GitLab answered HTTP 403"));

        var act = async () => await BuildPolicy().ExecuteAsync<int>(Instance, "test", _ => throw refusal, CancellationToken.None);

        (await act.ShouldThrowAsync<ProviderPlanRequirementException>()).ShouldBeSameAs(refusal);
    }

    [Fact]
    public async Task ExecuteAsync_exhausts_attempts_then_rethrows_last_exception()
    {
        var policy = BuildPolicy();
        var calls = 0;

        var act = async () => await policy.ExecuteAsync<int>(Instance, "test", _ =>
        {
            calls++;
            throw new HttpRequestException($"failure #{calls}");
        }, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        calls.ShouldBe(ExternalCallResilience.MaxAttempts);
        ex.Message.ShouldContain($"#{ExternalCallResilience.MaxAttempts}", customMessage: "should rethrow the LAST exception, not the first");
    }

    [Fact]
    public async Task ExecuteAsync_cancellation_propagates_without_retry()
    {
        var policy = BuildPolicy();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        var act = async () => await policy.ExecuteAsync<int>(Instance, "test", _ =>
        {
            calls++;
            cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }, cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();
        calls.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_isolates_rate_limit_buckets_per_instance()
    {
        // Two providers' buckets are independent; one hitting capacity must not affect the other.
        var policy = BuildPolicy();
        var instanceA = BuildInstance();
        var instanceB = BuildInstance();

        var a = await policy.ExecuteAsync(instanceA, "test", _ => Task.FromResult(1), CancellationToken.None);
        var b = await policy.ExecuteAsync(instanceB, "test", _ => Task.FromResult(2), CancellationToken.None);

        a.ShouldBe(1);
        b.ShouldBe(2);
    }

    [Theory]
    [InlineData(1, false)]   // 100 calls answered at once took 100 tokens: the next call is served at once
    [InlineData(3, true)]    // 100 calls that each needed 3 attempts sent 300 requests and took 300 tokens: the bucket is empty
    public async Task ExecuteAsync_charges_the_limiter_for_every_attempt(int attemptsPerCall, bool nextCallWaits)
    {
        // A retry is another request on the provider's quota. Charged once per call, a provider answering 5xx was sent three
        // requests for every token the bucket gave out.
        var policy = BuildPolicy();
        var instance = BuildInstance();

        await Task.WhenAll(Enumerable.Range(0, ExternalCallResilience.TokensPerMinute / 3).Select(_ => policy.ExecuteAsync(instance, "test", FailingTransiently(attemptsPerCall - 1), CancellationToken.None)));

        using var cancel = new CancellationTokenSource();
        var next = policy.ExecuteAsync(instance, "test", _ => Task.FromResult(0), cancel.Token);
        await Task.WhenAny(next, Task.Delay(300));

        next.IsCompleted.ShouldBe(!nextCallWaits, nextCallWaits ? "every attempt took a token, so the bucket is empty and the next call waits for the refill" : "a call answered at once takes one token");
        cancel.Cancel();

        if (nextCallWaits) await Should.ThrowAsync<OperationCanceledException>(next);
    }

    /// <summary>An operation that fails transiently <paramref name="failures"/> times, then answers.</summary>
    private static Func<CancellationToken, Task<int>> FailingTransiently(int failures)
    {
        var remaining = failures;

        return _ => Interlocked.Decrement(ref remaining) >= 0 ? throw new HttpRequestException("flake") : Task.FromResult(1);
    }

    [Fact]
    public async Task A_write_that_landed_is_adopted_even_when_the_limiter_fills_up_before_its_retry()
    {
        // The first attempt takes the bucket's last token, the provider applies the write, and while its answer is lost
        // fifty other callers fill the limiter's queue. Refusing the retry then would skip the probe that finds the write,
        // and answer "rate limited, retry" for a write that landed — which a caller re-sends as a duplicate.
        var policy = BuildPolicy();
        var instance = BuildInstance();
        await DrainAsync(policy, instance, ExternalCallResilience.TokensPerMinute - 1);

        var landed = false;
        var sends = 0;
        using var others = new CancellationTokenSource();
        var queued = new List<Task>();

        var write = policy.ExecuteNonIdempotentAsync<string>(instance, "merge", _ =>
        {
            sends++;
            if (landed) return Task.FromResult("sent-again");

            landed = true;
            queued.AddRange(Enumerable.Range(0, ExternalCallResilience.QueueLimit).Select(_ => policy.ExecuteAsync(instance, "other-caller", _ => Task.FromResult(1), others.Token)));
            throw new HttpRequestException("connection reset after the provider applied the write");
        }, _ => Task.FromResult<string?>(landed ? "effect-1" : null), CancellationToken.None);

        var completed = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10)));
        others.Cancel();
        await Task.WhenAll(queued.Select(q => q.ContinueWith(_ => { })));

        completed.ShouldBeSameAs(write, "a retry is never held for the next refill");
        (await write).ShouldBe("effect-1", "the write landed on the first attempt; its retry must find and adopt it");
        sends.ShouldBe(1);
    }

    [Fact]
    public async Task Only_a_calls_first_attempt_can_be_refused_by_the_limiter()
    {
        var policy = BuildPolicy();
        var instance = BuildInstance();
        await DrainAsync(policy, instance, ExternalCallResilience.TokensPerMinute);
        using var others = new CancellationTokenSource();
        var queued = Enumerable.Range(0, ExternalCallResilience.QueueLimit).Select(_ => policy.ExecuteAsync(instance, "other-caller", _ => Task.FromResult(1), others.Token)).ToList();

        await Should.ThrowAsync<ProviderRateLimitedException>(() => policy.ExecuteAsync(instance, "new-call", _ => Task.FromResult(1), CancellationToken.None));

        others.Cancel();
        await Task.WhenAll(queued.Select(q => q.ContinueWith(_ => { })));
    }

    // ── An agent run's share of a connection ──
    // One agent run may spend only its share of a connection's requests a minute, so the team's other users — the Pulls
    // tab, other runs — keep the rest of the bucket.

    [Fact]
    public void TokensPerMinutePerAgentRun_constant_pinned() => ExternalCallResilience.TokensPerMinutePerAgentRun.ShouldBe(100);

    [Fact]
    public async Task An_agent_run_past_its_share_is_refused_while_the_connections_other_callers_still_get_a_token()
    {
        var policy = BuildPolicy();
        var instance = BuildInstance();
        var run = Guid.NewGuid();

        using (AgentRunProviderScope.Enter(run))
        {
            await DrainAsync(policy, instance, ExternalCallResilience.TokensPerMinutePerAgentRun);

            var refusal = await Should.ThrowAsync<ProviderRateLimitedException>(() => policy.ExecuteAsync(instance, "list", _ => Task.FromResult(1), CancellationToken.None));
            refusal.Message.ShouldContain($"agent run {run} has used its share of {ExternalCallResilience.TokensPerMinutePerAgentRun} requests a minute");
        }

        (await policy.ExecuteAsync(instance, "pulls-tab", _ => Task.FromResult(7), CancellationToken.None)).ShouldBe(7, "a call outside any agent run is served from the rest of the bucket");

        using (AgentRunProviderScope.Enter(Guid.NewGuid()))
            (await policy.ExecuteAsync(instance, "another-run", _ => Task.FromResult(8), CancellationToken.None)).ShouldBe(8, "another run has a share of its own");
    }

    [Fact]
    public async Task An_agent_runs_retry_is_never_refused_by_its_share()
    {
        // A retry follows a request already sent — possibly a write that landed — so like the bucket, the share refuses only a first attempt.
        var policy = BuildPolicy();
        var instance = BuildInstance();

        using var scope = AgentRunProviderScope.Enter(Guid.NewGuid());
        await DrainAsync(policy, instance, ExternalCallResilience.TokensPerMinutePerAgentRun - 1);

        (await policy.ExecuteAsync(instance, "flaky", FailingTransiently(2), CancellationToken.None)).ShouldBe(1, "the call's first attempt took the share's last request; its two retries went out");
    }

    [Theory]
    [InlineData(59, false)]   // within the minute: still spent
    [InlineData(60, true)]    // a minute on: the share is whole again
    public void A_runs_share_is_whole_again_a_minute_after_it_began(int secondsLater, bool admitted)
    {
        var share = new AgentRunProviderShare(perMinute: 2);
        var instance = Guid.NewGuid();
        var run = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        share.TryCharge(instance, run, start).ShouldBeTrue();
        share.TryCharge(instance, run, start.AddSeconds(30)).ShouldBeTrue();
        share.TryCharge(instance, run, start.AddSeconds(31)).ShouldBeFalse();

        share.TryCharge(instance, run, start.AddSeconds(secondsLater)).ShouldBe(admitted);
        share.TryCharge(Guid.NewGuid(), run, start.AddSeconds(31)).ShouldBeTrue("a share is per connection");
    }

    /// <summary>Spend <paramref name="calls"/> tokens of <paramref name="instance"/>'s bucket with calls answered at once.</summary>
    private static async Task DrainAsync(ExternalCallResilience policy, ProviderInstance instance, int calls)
    {
        for (var i = 0; i < calls; i++) await policy.ExecuteAsync(instance, "drain", _ => Task.FromResult(1), CancellationToken.None);
    }

    // ── Non-idempotent writes: never re-send a write that may already have landed ──
    // A timeout, a dropped connection or a 5xx can arrive AFTER the provider applied the write. The blind
    // retry above would apply it again; ExecuteNonIdempotentAsync asks the provider before re-sending.

    [Theory]
    [InlineData("timeout")]   // TaskCanceledException — the request went out, no answer arrived in time
    [InlineData("reset")]     // HttpRequestException — the connection dropped mid-response
    [InlineData("5xx")]       // a gateway answered 502 after the backend committed
    public async Task ExecuteNonIdempotentAsync_adopts_a_write_that_landed_before_its_attempt_failed(string failure)
    {
        var target = new FakeWriteTarget();

        var result = await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((true, AmbiguousFailure(failure)), (true, null)), target.FindAsync, CancellationToken.None);

        result.ShouldBe("effect-1");
        target.Effects.Count.ShouldBe(1, "the write landed on the first attempt — re-sending it would have applied it a second time");
        target.WritesSent.ShouldBe(1);
        target.Probes.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_resends_when_the_failed_attempt_left_no_effect()
    {
        // Connection refused: the write never reached the provider. The probe finds nothing, so the write goes out again.
        var target = new FakeWriteTarget();

        var result = await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((false, new HttpRequestException("Connection refused")), (true, null)), target.FindAsync, CancellationToken.None);

        result.ShouldBe("effect-1");
        target.Effects.Count.ShouldBe(1);
        target.WritesSent.ShouldBe(2);
        target.Probes.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_applies_once_when_the_write_first_lands_on_the_second_attempt()
    {
        var target = new FakeWriteTarget();

        var result = await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((false, new FakeSdkException(503)), (true, new TaskCanceledException("timeout")), (true, null)), target.FindAsync, CancellationToken.None);

        result.ShouldBe("effect-1");
        target.Effects.Count.ShouldBe(1, "attempt 1 left nothing, attempt 2 landed and timed out — attempt 3 must adopt it, not send a third write");
        target.WritesSent.ShouldBe(2);
        target.Probes.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_does_not_probe_when_the_first_attempt_answers()
    {
        var target = new FakeWriteTarget();

        var result = await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((true, null)), target.FindAsync, CancellationToken.None);

        result.ShouldBe("effect-1");
        target.WritesSent.ShouldBe(1);
        target.Probes.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_neither_probes_nor_retries_a_non_transient_failure()
    {
        var target = new FakeWriteTarget();

        var act = async () => await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((false, new InvalidOperationException("rejected"))), target.FindAsync, CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        target.WritesSent.ShouldBe(1);
        target.Probes.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_keeps_the_attempt_budget()
    {
        var target = new FakeWriteTarget();

        var act = async () => await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((false, new HttpRequestException("failure #1")), (false, new HttpRequestException("failure #2")), (false, new HttpRequestException("failure #3"))), target.FindAsync, CancellationToken.None);

        var ex = await act.ShouldThrowAsync<HttpRequestException>();
        ex.Message.ShouldContain($"#{ExternalCallResilience.MaxAttempts}");
        target.WritesSent.ShouldBe(ExternalCallResilience.MaxAttempts);
        target.Probes.ShouldBe(ExternalCallResilience.MaxAttempts - 1);
        target.Effects.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_retries_a_probe_that_fails_transiently_instead_of_resending()
    {
        var target = new FakeWriteTarget();
        target.ProbeFailures.Enqueue(new HttpRequestException("probe flake"));

        var result = await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((true, new TaskCanceledException("timeout")), (true, null)), target.FindAsync, CancellationToken.None);

        result.ShouldBe("effect-1");
        target.Effects.Count.ShouldBe(1, "a probe that could not answer is no evidence the write is absent — it must be asked again, not skipped");
        target.WritesSent.ShouldBe(1);
        target.Probes.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteNonIdempotentAsync_surfaces_a_probe_that_fails_permanently_instead_of_resending()
    {
        var target = new FakeWriteTarget();
        target.ProbeFailures.Enqueue(new InvalidOperationException("cannot list"));

        var act = async () => await BuildPolicy().ExecuteNonIdempotentAsync(Instance, "test", target.Scripted((true, new TaskCanceledException("timeout")), (true, null)), target.FindAsync, CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        target.WritesSent.ShouldBe(1);
        target.Effects.Count.ShouldBe(1);
    }

    [Fact]
    public void DescribeEffect_names_the_adopted_write_whatever_SDK_made_it()
    {
        ExternalCallResilienceExtensions.DescribeEffect(new SdkComment(42, "https://github.test/acme/api/issues/7#issuecomment-42")).ShouldBe(("42", "https://github.test/acme/api/issues/7#issuecomment-42"));
        ExternalCallResilienceExtensions.DescribeEffect(new SdkIssue(9001, 5, "https://github.test/acme/api/issues/5")).ShouldBe(("5", "https://github.test/acme/api/issues/5"), "the number people use beats the database id");
        ExternalCallResilienceExtensions.DescribeEffect(new CodeSpace.Messages.Dtos.Providers.RemotePullRequestMergeResult { Merged = true, Sha = "9f8e7d" }).ShouldBe(("9f8e7d", (string?)null));
        ExternalCallResilienceExtensions.DescribeEffect(new object()).ShouldBe(((string?)null, (string?)null));
    }

    private sealed record SdkComment(long Id, string HtmlUrl);

    private sealed record SdkIssue(long Id, int Number, string HtmlUrl);

    private static Exception AmbiguousFailure(string kind) => kind switch
    {
        "timeout" => new TaskCanceledException("the request went out; no answer before the timeout"),
        "reset" => new HttpRequestException("the connection dropped mid-response"),
        "5xx" => new FakeSdkException(502),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>
    /// Stands in for the provider behind a write: counts every write sent and every effect applied, and answers
    /// the probe from what it applied. Each scripted attempt says whether the write lands and how the attempt
    /// ends — null means the provider answered.
    /// </summary>
    private sealed class FakeWriteTarget
    {
        private readonly List<string> _effects = new();

        public int WritesSent { get; private set; }
        public int Probes { get; private set; }
        public IReadOnlyList<string> Effects => _effects;
        public Queue<Exception> ProbeFailures { get; } = new();

        public Func<CancellationToken, Task<string>> Scripted(params (bool Lands, Exception? Failure)[] attempts) => _ =>
        {
            var (lands, failure) = attempts[WritesSent++];

            if (lands) _effects.Add($"effect-{_effects.Count + 1}");
            if (failure != null) throw failure;

            return Task.FromResult(_effects[^1]);
        };

        public Task<string?> FindAsync(CancellationToken _)
        {
            Probes++;

            if (ProbeFailures.TryDequeue(out var failure)) throw failure;

            return Task.FromResult(_effects.LastOrDefault());
        }
    }

    private static ExternalCallResilience BuildPolicy() => new(new NoopErrorMapperRegistry(), NullLogger<ExternalCallResilience>.Instance);

    /// <summary>
    /// Resilience tests pre-date the error-mapper layer and care only about retry/rate-limit
    /// semantics. A no-op mapper keeps those tests focused; scope-mapping is covered by the
    /// dedicated GitHubErrorMapperTests / GitLabErrorMapperTests.
    /// </summary>
    private sealed class NoopErrorMapperRegistry : CodeSpace.Core.Services.Providers.Errors.IProviderErrorMapperRegistry
    {
        public CodeSpace.Core.Services.Providers.Errors.IProviderErrorMapper? Get(ProviderKind kind) => null;
    }

    private static ProviderInstance BuildInstance() => new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Provider = ProviderKind.GitHub,
        DisplayName = "test",
        BaseUrl = "https://test.local"
    };

    // ── Duck-typed SDK exception stubs (mimics Octokit.ApiException and NGitLab.GitLabException) ──

    private sealed class FakeSdkException : Exception
    {
        public FakeSdkException(int statusCode) : base($"HTTP {statusCode}") { StatusCode = statusCode; }
        public int StatusCode { get; }
    }

    private sealed class FakeSdkExceptionWithEnumStatusCode : Exception
    {
        public FakeSdkExceptionWithEnumStatusCode(HttpStatusCode statusCode) : base($"HTTP {(int)statusCode}") { StatusCode = statusCode; }
        public HttpStatusCode StatusCode { get; }
    }
}
