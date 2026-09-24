using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Providers.Resilience;

/// <summary>
/// Wraps every external SDK call (Octokit, NGitLab, future Bitbucket SDK) with two layers:
/// per-ProviderInstance token-bucket rate limiting and exponential-backoff retry on transient
/// failures (HttpRequestException, TaskCanceledException, 5xx HTTP status).
/// Provider classes call this for every method that hits the wire. Streaming methods
/// (IAsyncEnumerable) are intentionally NOT wrapped here — caller handles per-page retry.
/// A write that must not land twice (a create, a merge) goes through
/// <see cref="ExternalCallResilienceExtensions.ExecuteNonIdempotentAsync{T}"/> instead, which
/// asks the provider whether a failed attempt landed before it sends the write again.
/// </summary>
public interface IExternalCallResilience
{
    Task<T> ExecuteAsync<T>(ProviderInstance instance, string operationName, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);

    Task ExecuteAsync(ProviderInstance instance, string operationName, Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}
