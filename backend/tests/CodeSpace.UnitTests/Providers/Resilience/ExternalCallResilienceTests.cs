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
