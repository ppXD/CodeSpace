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

    /// <summary>
    /// The most requests one agent run may send through one provider instance a minute: a third of
    /// <see cref="TokensPerMinute"/>, so a run listing as fast as it can still leaves the connection's other users — the
    /// Pulls tab, other runs — the rest of the bucket. Charged per request, on a call's first attempt, to the run named by
    /// <see cref="AgentRunProviderScope"/>; past it the call is refused before it is sent. Pinned by test.
    /// </summary>
    public const int TokensPerMinutePerAgentRun = 100;

    private readonly ConcurrentDictionary<Guid, TokenBucketRateLimiter> _limitersByInstance = new();
    private readonly AgentRunProviderShare _agentRunShare = new(TokensPerMinutePerAgentRun);
    private readonly IProviderErrorMapperRegistry _errorMappers;
    private readonly ILogger<ExternalCallResilience> _logger;

    public ExternalCallResilience(IProviderErrorMapperRegistry errorMappers, ILogger<ExternalCallResilience> logger)
    {
        _errorMappers = errorMappers;
        _logger = logger;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with retries. Every attempt is charged to the instance's limiter first: an attempt
    /// is a request on the provider's quota, a retry as much as the first (<see cref="ChargeAttemptAsync"/>). A provider call
    /// that sends several requests is several calls through here, one per request.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(ProviderInstance instance, string operationName, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await ChargeAttemptAsync(instance, operationName, attempt, cancellationToken).ConfigureAwait(false);

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
        // Typed where it was raised: it names the plan and the way around it. Re-labelled as a bare status it would read
        // as a credential problem, so it leaves as it came.
        if (ex is ProviderPlanRequirementException) return;

        var status = ExtractStatusCode(ex);
        if (status == null) return;

        _logger.LogWarning(ex, "Provider {Provider} returned HTTP {StatusCode} for '{Operation}' on instance {InstanceId}: {Message}", instance.Provider, status, operationName, instance.Id, ex.Message);
        throw new ProviderApiException(instance.Provider, status.Value, operationName, ex.Message, ex);
    }

    /// <summary>
    /// Charge one attempt. A call's first attempt is charged to the calling agent run's share, then waits its turn for the
    /// instance's token and may be refused (<see cref="ProviderRateLimitedException"/>): nothing has been sent yet. A retry
    /// follows a request already sent — perhaps a write that landed, which the retry is about to find and adopt — so it is
    /// never refused or held: it takes a token when one is free and otherwise goes out uncharged. A call sends at most
    /// <see cref="MaxAttempts"/> requests, so a spent bucket lets at most that many less one through per call.
    /// </summary>
    private async Task ChargeAttemptAsync(ProviderInstance instance, string operationName, int attempt, CancellationToken cancellationToken)
    {
        if (attempt > 1)
        {
            ChargeRetry(instance);
            return;
        }

        ChargeAgentRunShare(instance, operationName);

        await AcquireTokenAsync(instance, operationName, cancellationToken).ConfigureAwait(false);
    }

    private void ChargeRetry(ProviderInstance instance)
    {
        using var lease = Limiter(instance).AttemptAcquire(1);
    }

    /// <summary>Refuses the call when the agent run making it has spent its share of the instance this minute. A call outside any agent run has no share.</summary>
    private void ChargeAgentRunShare(ProviderInstance instance, string operationName)
    {
        if (AgentRunProviderScope.Current is not { } runId || _agentRunShare.TryCharge(instance.Id, runId, DateTimeOffset.UtcNow)) return;

        _logger.LogWarning("Agent run {RunId} has used its share of {Share} requests a minute on provider instance {InstanceId}; '{Operation}' was not sent", runId, TokensPerMinutePerAgentRun, instance.Id, operationName);

        throw ProviderRateLimitedException.ForAgentRun(instance.Id, operationName, runId, TokensPerMinutePerAgentRun);
    }

    private async Task AcquireTokenAsync(ProviderInstance instance, string operationName, CancellationToken cancellationToken)
    {
        using var lease = await Limiter(instance).AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        if (!lease.IsAcquired) throw new ProviderRateLimitedException(instance.Id, operationName);
    }

    private TokenBucketRateLimiter Limiter(ProviderInstance instance) => _limitersByInstance.GetOrAdd(instance.Id, _ => BuildLimiter());

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
    /// A network blip looks different per transport: an HttpClient SDK (Octokit) throws
    /// HttpRequestException, while NGitLab speaks HttpWebRequest — a connection it never got
    /// an answer on is a WebException without a response, and an answer cut short mid-body is
    /// an HttpIOException.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (exception is HttpRequestException or HttpIOException) return true;
        if (exception is WebException { Response: null }) return true;
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
