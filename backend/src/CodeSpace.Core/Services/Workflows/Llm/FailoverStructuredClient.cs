using CodeSpace.Core.Services.Agents.ModelCredentials;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// L4 of the gateway-fault backstop: ONE structured call tried across the team's pool candidates in a DETERMINISTIC
/// order. A candidate that fails with a transient wire fault (5xx / timeout / 429) is skipped — the next provider's
/// model is tried with ITS credential — and the skip is recorded on the completion's <see cref="StructuredLLMCompletion.FailedOver"/>
/// trail (and logged at Warning) so the answering model is never mistaken for the resolved one. Anything else propagates
/// immediately: an auth failure, a bad request, a context overflow or a malformed body is a fault of THIS request or
/// THIS credential, and hopping providers on it would hide an operator problem behind a lucky alternate. The LAST
/// candidate's transient fault propagates verbatim (typed) — when the whole pool is down the honest answer is the
/// typed failure the callers already park on, never a silent retry loop.
///
/// <para>D1: a candidate the run's cost cap cannot price (<see cref="Agents.Cost.UnpricedModelUnderCapException"/>) is skipped the
/// same way. The refusal happens BEFORE the call, so nothing was spent and a priced alternate is still a good
/// answer; only when EVERY candidate is unpriced does the last one's refusal propagate and the run fail closed.</para>
///
/// <para>Once a hop has happened, the wire-health fault that caused it — not whatever the substitute does next — is the
/// call's real story. Two places honour that. A completion carries it on
/// <see cref="StructuredLLMCompletion.FailedOverCause"/>, so a caller that cannot USE the substitute's answer surfaces
/// the throttle instead of reading the answer as a capability verdict. And an EXHAUSTED pool whose last candidate
/// failed for a model / credential reason re-throws that same wire-health fault, because a substitute's 400 is not
/// why the caller has no answer — the throttle on its own model is.</para>
/// </summary>
public sealed class FailoverStructuredClient : IStructuredLLMClient
{
    private readonly IReadOnlyList<(IStructuredLLMClient Client, ModelPoolPick Pick)> _candidates;
    private readonly ILogger? _logger;

    /// <summary>The logger is optional because one construction site has none to give: <see cref="InProcessStructuredModel"/> is a static resolver shared by the planner / classifier lanes. Without it a hop is still recorded on the completion's trail — it just isn't announced live.</summary>
    public FailoverStructuredClient(IReadOnlyList<(IStructuredLLMClient Client, ModelPoolPick Pick)> candidates, ILogger? logger = null)
    {
        if (candidates.Count == 0) throw new ArgumentException("A failover client needs at least one candidate.", nameof(candidates));
        _candidates = candidates;
        _logger = logger;
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
        LlmApiException? wireHealth = null;

        for (var i = 0; i < _candidates.Count; i++)
        {
            var (client, pick) = _candidates[i];
            var attempt = request with { Model = pick.ModelId, Credential = pick.Credential };

            try
            {
                var completion = await client.CompleteStructuredAsync(attempt, cancellationToken).ConfigureAwait(false);
                return trail.Count == 0 ? completion : completion with { FailedOver = trail, FailedOverCause = wireHealth };
            }
            catch (LlmApiException ex) when (IsFailoverWorthy(ex.Category) && i < _candidates.Count - 1)
            {
                wireHealth ??= ex;
                Skip(trail, client, pick, Describe(ex));
            }
            catch (LlmApiException ex) when (i == _candidates.Count - 1 && !IsFailoverWorthy(ex.Category) && wireHealth is not null)
            {
                // The pool is EXHAUSTED and the last candidate failed for a MODEL / CREDENTIAL reason — a category the
                // callers fail CLOSED on (the supervisor decider turns it into a clean terminal stop). But this call
                // only ever reached that candidate because the caller's OWN model was throttled or down, so surfacing
                // the substitute's fault would end a run on "the model could not decide" when the truth is a 429.
                // Re-throw the wire-health fault that started the hop — its category / status / Retry-After are what
                // the bounded retry and the node's infra park act on — keeping the substitute's fault as the inner.
                Skip(trail, client, pick, Describe(ex));

                throw new LlmApiException(wireHealth.Provider, wireHealth.StatusCode, wireHealth.Category,
                    $"{wireHealth.ProviderMessage} — and no pool alternate could answer either ({string.Join(" | ", trail)})", wireHealth.RetryAfter, ex);
            }
            catch (Agents.Cost.UnpricedModelUnderCapException) when (i < _candidates.Count - 1)
            {
                // D1: this candidate has no price and the run declares a cost cap, so the budget guard refused it
                // BEFORE any call — nothing was spent, and a PRICED alternate is still a perfectly good answer. Skip
                // it exactly like a transient fault rather than letting the refusal escape the loop, which would
                // regress the pool failover (#1737/#1738) into "the first candidate's missing price kills the run".
                // The LAST candidate's refusal still propagates, so a pool where NOTHING is priced fails closed.
                Skip(trail, client, pick, "unpriced under the run's cost cap");
            }
        }

        throw new InvalidOperationException("unreachable: the last candidate either returned or threw");
    }

    /// <summary>Record one candidate's outcome on the trail AND announce it at Warning — a silent hop is how a throttled pool reads as ordinary model behaviour in a log.</summary>
    private void Skip(List<string> trail, IStructuredLLMClient client, ModelPoolPick pick, string outcome)
    {
        trail.Add($"{client.Provider}:{pick.ModelId} — {outcome}");

        _logger?.LogWarning("Structured model failover skipped candidate {Provider}:{Model} — {Outcome}", client.Provider, pick.ModelId, outcome);
    }

    private static string Describe(LlmApiException ex) => $"{ex.Category}{(ex.StatusCode is { } status ? $" {status}" : "")}";
}
