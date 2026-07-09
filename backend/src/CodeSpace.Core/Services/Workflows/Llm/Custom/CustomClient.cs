using System.Net.Http;
using System.Runtime.CompilerServices;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;

namespace CodeSpace.Core.Services.Workflows.Llm.Custom;

/// <summary>
/// The in-process LLM client for a credential tagged <c>Provider == "Custom"</c> (Rule 18.3 — a sibling provider
/// module). A "Custom" endpoint is an operator's own OpenAI-API-compatible gateway (LiteLLM / vLLM / OpenRouter / a
/// self-hosted relay), so it speaks the SAME wire as <see cref="OpenAiClient"/> — this delegates every call to that wire
/// verbatim and re-tags the provider as <c>"Custom"</c>, so the registry resolves it for a Custom credential
/// (<c>c.Provider == "Custom"</c>) and the model + key come entirely from that credential's pool row (model id +
/// decrypted key + the custom BaseUrl). No duplicated wire logic — a decorator over the proven OpenAI client.
///
/// <para>This is what lets a team whose pool is ALL Custom-tagged models drive the IN-PROCESS plane (the supervisor
/// decider, the planner, the effort classifier), not just the agent CLI-harness plane — so "Custom endpoints run all the
/// way to the supervisor". The structured client makes <c>"Custom"</c> an eligible brain provider (the brain auto-pick +
/// the decider's provider-match both flow through it).</para>
/// </summary>
public sealed class CustomClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
{
    private readonly OpenAiClient _wire;

    public CustomClient(IHttpClientFactory httpClientFactory) => _wire = new OpenAiClient(httpClientFactory);

    public string Provider => "Custom";

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Retagged(() => _wire.CompleteAsync(request, cancellationToken));

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
        Retagged(() => _wire.CompleteStructuredAsync(request, cancellationToken));

    /// <summary>Delegate the OpenAI-wire stream, re-tagging any transport error to "Custom" AS IT SURFACES mid-enumeration. The <see cref="Retagged{T}"/> helper wraps a <c>Task</c>, which an <c>IAsyncEnumerable</c> can't return through — so the re-tag guards each <c>MoveNextAsync</c> here (the error is thrown lazily on the first/next pull, not at call time).</summary>
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = _wire.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            LlmStreamEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                current = enumerator.Current;
            }
            catch (LlmApiException ex)
            {
                throw new LlmApiException("Custom", ex.StatusCode, ex.Category, ex.ProviderMessage, ex.RetryAfter, ex);
            }

            yield return current;
        }
    }

    /// <summary>Run the inner OpenAI wire and re-tag any transport error as provider "Custom" (preserving the status / category / message / retry-after), so a misconfigured Custom gateway surfaces "Custom API error" against the operator's OWN endpoint — not a misleading "OpenAI" label. The category is preserved, so the decider's capability-miss filter is unaffected.</summary>
    private static async Task<T> Retagged<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (LlmApiException ex)
        {
            throw new LlmApiException("Custom", ex.StatusCode, ex.Category, ex.ProviderMessage, ex.RetryAfter, ex);
        }
    }
}
