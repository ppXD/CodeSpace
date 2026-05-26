using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Providers.Resilience;

public sealed class ExternalCallResilience : IExternalCallResilience, ISingletonDependency
{
    /// <summary>Total attempts (including the first call). Pinned by test — operator dashboards depend on this number.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Base delay between retries. attempt N waits BaseDelayMs * 2^(N-1).</summary>
    public const int BaseDelayMs = 200;

    /// <summary>Token-bucket replenishment rate per provider instance. 300/min = 5/sec, conservative for both GitHub PAT (5000/hr) and GitLab default tiers.</summary>
    public const int TokensPerMinute = 300;

    /// <summary>Max calls held while bucket refills. Smooths burst load; oldest-first.</summary>
    public const int QueueLimit = 50;

    private readonly ConcurrentDictionary<Guid, TokenBucketRateLimiter> _limitersByInstance = new();
    private readonly IProviderErrorMapperRegistry _errorMappers;
    private readonly ILogger<ExternalCallResilience> _logger;

    public ExternalCallResilience(IProviderErrorMapperRegistry errorMappers, ILogger<ExternalCallResilience> logger)
    {
        _errorMappers = errorMappers;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(ProviderInstance instance, string operationName, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await AcquireTokenAsync(instance, operationName, cancellationToken).ConfigureAwait(false);

        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;
                if (attempt == MaxAttempts) break;

                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(ex, "External call '{Operation}' attempt {Attempt}/{MaxAttempts} on instance {InstanceId} failed (transient); retrying in {DelayMs}ms", operationName, attempt, MaxAttempts, instance.Id, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-transient: don't retry. Translate in order of specificity —
                //   1. 403/insufficient_scope → typed ProviderInsufficientScopeException (422).
                //   2. Any other SDK exception with a duck-typed StatusCode → ProviderApiException
                //      so GlobalExceptionFilter returns a real 4xx, not a 500.
                TranslateAndThrowIfScopeIssue(instance, operationName, ex);
                TranslateAndThrowIfProviderApi(instance, operationName, ex);
                throw;
            }
        }

        // All retries exhausted on a transient — still try the mappers in case the final
        // attempt returned an SDK-specific code the frontend can act on.
        TranslateAndThrowIfScopeIssue(instance, operationName, lastException!);
        TranslateAndThrowIfProviderApi(instance, operationName, lastException!);
        throw lastException!;
    }

    public async Task ExecuteAsync(ProviderInstance instance, string operationName, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(instance, operationName, async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the per-provider error mapper whether <paramref name="ex"/> is an insufficient-scope
    /// error. If yes, throw the typed exception (replacing the cryptic SDK exception). If no,
    /// returns silently — caller re-throws the original.
    /// </summary>
    private void TranslateAndThrowIfScopeIssue(ProviderInstance instance, string operationName, Exception ex)
    {
        var mapper = _errorMappers.Get(instance.Provider);
        if (mapper == null) return;

        var typed = mapper.TryMapInsufficientScope(ex, operationName);
        if (typed == null) return;

        _logger.LogWarning(ex, "Provider {Provider} returned insufficient_scope for '{Operation}' on instance {InstanceId}; missing scopes: {MissingScopes}", instance.Provider, operationName, instance.Id, string.Join(", ", typed.MissingScopes));
        throw typed;
    }

    /// <summary>
    /// Re-throws any SDK exception with a known HTTP status code as a typed
    /// <see cref="ProviderApiException"/>. The check is duck-typed (StatusCode property)
    /// so the resilience layer stays SDK-agnostic — Octokit, NGitLab, and any future
    /// provider all surface 4xx the same way to GlobalExceptionFilter.
    /// </summary>
    private void TranslateAndThrowIfProviderApi(ProviderInstance instance, string operationName, Exception ex)
    {
        var status = ExtractStatusCode(ex);
        if (status == null) return;

        _logger.LogWarning(ex, "Provider {Provider} returned HTTP {StatusCode} for '{Operation}' on instance {InstanceId}: {Message}", instance.Provider, status, operationName, instance.Id, ex.Message);
        throw new ProviderApiException(instance.Provider, status.Value, operationName, ex.Message, ex);
    }

    private async Task AcquireTokenAsync(ProviderInstance instance, string operationName, CancellationToken cancellationToken)
    {
        var limiter = _limitersByInstance.GetOrAdd(instance.Id, _ => BuildLimiter());
        using var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        if (!lease.IsAcquired) throw new ProviderRateLimitedException(instance.Id, operationName);
    }

    private static TokenBucketRateLimiter BuildLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = TokensPerMinute,
        TokensPerPeriod = TokensPerMinute,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        QueueLimit = QueueLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true
    });

    /// <summary>Exponential: attempt 1 → 200ms, 2 → 400ms, 3 → 800ms.</summary>
    public static TimeSpan ComputeBackoff(int attempt) => TimeSpan.FromMilliseconds(BaseDelayMs * Math.Pow(2, attempt - 1));

    /// <summary>
    /// Transient = retry helps. Network blip / timeout / 5xx server overload qualify;
    /// 4xx (auth, not-found) do not — retrying just wastes quota. SDK exception types
    /// (Octokit.ApiException, NGitLab.GitLabException) all expose a StatusCode property
    /// so duck-typed reflection covers every provider without per-SDK exception mapping.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (exception is HttpRequestException) return true;
        if (exception is TaskCanceledException) return true;

        var status = ExtractStatusCode(exception);
        return status is >= 500 and < 600;
    }

    private static int? ExtractStatusCode(Exception exception)
    {
        var prop = exception.GetType().GetProperty("StatusCode");
        if (prop == null) return null;

        var value = prop.GetValue(exception);
        return value switch
        {
            HttpStatusCode hsc => (int)hsc,
            int i => i,
            _ => null
        };
    }
}
