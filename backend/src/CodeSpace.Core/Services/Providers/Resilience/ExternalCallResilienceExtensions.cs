using CodeSpace.Core.Persistence.Entities;
using Serilog;

namespace CodeSpace.Core.Services.Providers.Resilience;

public static class ExternalCallResilienceExtensions
{
    private static readonly string[] EffectIdProperties = { "Number", "Iid", "IssueId", "NoteId", "Id", "ExternalId", "Sha" };

    private static readonly string[] EffectUrlProperties = { "HtmlUrl", "WebUrl" };

    /// <summary>
    /// For a write the provider must not apply twice — a create (comment, issue, review, pull request) or a
    /// one-way state transition (merge, approve). <see cref="IExternalCallResilience.ExecuteAsync{T}"/> retries
    /// a transient failure by sending the call again, but a timeout, a dropped connection or a 5xx can arrive
    /// AFTER the provider applied the write, and re-sending it then applies it a second time. Here every retry
    /// first asks <paramref name="findExisting"/> — a read — whether an earlier attempt landed, and returns that
    /// effect instead of sending again; only when nothing is found does the write go out again.
    ///
    /// <para>Composed on <c>ExecuteAsync</c> rather than copied from it, so the attempt budget, backoff, rate
    /// limit and error translation are the same ones every other call gets. The probe runs inside an attempt:
    /// a probe that fails transiently spends that attempt and is asked again on the next — it never falls
    /// through to a blind re-send — and one that fails permanently surfaces as the call's failure. Each adoption
    /// logs one line naming the operation, the provider and what it took.</para>
    /// </summary>
    public static async Task<T> ExecuteNonIdempotentAsync<T>(this IExternalCallResilience resilience, ProviderInstance instance, string operationName, Func<CancellationToken, Task<T>> operation, Func<CancellationToken, Task<T?>> findExisting, CancellationToken cancellationToken) where T : class
    {
        var sent = false;

        return await resilience.ExecuteAsync(instance, operationName, async ct =>
        {
            if (sent && await findExisting(ct).ConfigureAwait(false) is { } existing) return Adopted(instance, operationName, existing);

            sent = true;

            return await operation(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One line per adoption, so the log says whether a retry sent the write again or took the one that landed.</summary>
    private static T Adopted<T>(ProviderInstance instance, string operationName, T effect) where T : class
    {
        var (id, url) = DescribeEffect(effect);

        Log.Information("External call '{Operation}' on {Provider} instance {InstanceId} adopted the write an earlier attempt landed ({EffectId} {EffectUrl}) instead of sending it again", operationName, instance.Provider, instance.Id, id, url);

        return effect;
    }

    /// <summary>
    /// The adopted write's id and link, read by property name — duck-typed like <c>ExternalCallResilience</c>'s
    /// StatusCode lookup, so the line names an Octokit comment, an NGitLab note or a CodeSpace DTO alike. The
    /// number people use (issue / pull request number, GitLab iid) wins over a database id.
    /// </summary>
    internal static (string? Id, string? Url) DescribeEffect(object effect) => (FirstValue(effect, EffectIdProperties), FirstValue(effect, EffectUrlProperties));

    private static string? FirstValue(object effect, IEnumerable<string> propertyNames) =>
        propertyNames.Select(name => effect.GetType().GetProperty(name)?.GetValue(effect)?.ToString()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
