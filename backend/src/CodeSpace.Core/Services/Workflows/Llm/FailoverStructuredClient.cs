using CodeSpace.Core.Services.Agents.ModelCredentials;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// L4 of the gateway-fault backstop: ONE structured call tried across the team's pool candidates in a DETERMINISTIC
/// order. A candidate that fails with a transient wire fault (5xx / timeout / 429) is skipped — the next provider's
/// model is tried with ITS credential — and the skip is recorded on the completion's <see cref="StructuredLLMCompletion.FailedOver"/>
/// trail so the answering model is never mistaken for the resolved one. Anything else propagates immediately: an
/// auth failure, a bad request, a context overflow or a malformed body is a fault of THIS request or THIS
/// credential, and hopping providers on it would hide an operator problem behind a lucky alternate. The LAST
/// candidate's transient fault propagates verbatim (typed) — when the whole pool is down the honest answer is the
/// typed failure the callers already park on, never a silent retry loop.
/// </summary>
public sealed class FailoverStructuredClient : IStructuredLLMClient
{
    private readonly IReadOnlyList<(IStructuredLLMClient Client, ModelPoolPick Pick)> _candidates;

    public FailoverStructuredClient(IReadOnlyList<(IStructuredLLMClient Client, ModelPoolPick Pick)> candidates)
    {
        if (candidates.Count == 0) throw new ArgumentException("A failover client needs at least one candidate.", nameof(candidates));
        _candidates = candidates;
    }

    /// <summary>The FIRST candidate's provider — what a caller that reads the provider sees (and what its request was built for).</summary>
    public string Provider => _candidates[0].Client.Provider;

    /// <summary>The ordered candidates — exposed for tests and diagnostics; the resolver builds it in registry order.</summary>
    public IReadOnlyList<(IStructuredLLMClient Client, ModelPoolPick Pick)> Candidates => _candidates;

    /// <summary>The wire faults worth hopping providers on — the gateway was unhealthy or throttled, and no billable completion was produced. Everything else is a fault of the request or the credential.</summary>
    public static bool IsFailoverWorthy(LlmErrorCategory category) => category is LlmErrorCategory.Transient or LlmErrorCategory.RateLimited;

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var trail = new List<string>();

        for (var i = 0; i < _candidates.Count; i++)
        {
            var (client, pick) = _candidates[i];
            var attempt = request with { Model = pick.ModelId, Credential = pick.Credential };

            try
            {
                var completion = await client.CompleteStructuredAsync(attempt, cancellationToken).ConfigureAwait(false);
                return trail.Count == 0 ? completion : completion with { FailedOver = trail };
            }
            catch (LlmApiException ex) when (IsFailoverWorthy(ex.Category) && i < _candidates.Count - 1)
            {
                trail.Add($"{client.Provider}:{pick.ModelId} — {ex.Category}{(ex.StatusCode is { } status ? $" {status}" : "")}");
            }
        }

        throw new InvalidOperationException("unreachable: the last candidate either returned or threw");
    }
}
