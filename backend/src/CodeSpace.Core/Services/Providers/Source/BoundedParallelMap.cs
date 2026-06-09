using System.Collections.Concurrent;

namespace CodeSpace.Core.Services.Providers.Source;

/// <summary>
/// Runs a best-effort async lookup over a set of keys with a bounded degree of parallelism, collecting the
/// successful results into a map. A key whose selector throws or returns null is simply omitted — the call
/// never fails as a whole. This is the engine behind per-entry "last commit" enrichment, where each tree
/// entry costs one provider call and we cap concurrency to stay polite to rate limits.
/// </summary>
public static class BoundedParallelMap
{
    public static async Task<IReadOnlyDictionary<string, TValue>> RunAsync<TValue>(
        IEnumerable<string> keys,
        int maxDegreeOfParallelism,
        Func<string, CancellationToken, Task<TValue?>> selector,
        CancellationToken cancellationToken)
        where TValue : class
    {
        var results = new ConcurrentDictionary<string, TValue>();
        using var gate = new SemaphoreSlim(Math.Max(1, maxDegreeOfParallelism));

        var tasks = keys.Distinct().Select(async key =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var value = await selector(key, cancellationToken).ConfigureAwait(false);
                if (value != null) results[key] = value;
            }
            catch
            {
                // Best-effort: a key that fails (rate-limited, deleted, transient) is omitted from the map.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
