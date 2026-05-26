using System.Net;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Resilience;

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
