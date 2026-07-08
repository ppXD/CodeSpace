using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Shared HTTP transport mechanics for the LLM clients — sends an already-built <see cref="HttpRequestMessage"/>
/// (each client owns its own URL + auth headers + body) and turns the outcome into either the deserialized success
/// shape or a typed <see cref="LlmApiException"/>. Centralising this kills the per-client drift in status
/// classification + timeout translation: every wire failure becomes a machine-actionable <see cref="LlmErrorCategory"/>
/// instead of an untyped <see cref="InvalidOperationException"/> with the status buried in prose.
///
/// <para>Per-call <see cref="HttpRequestMessage"/> headers (NOT mutation of the singleton-resolved client's
/// <c>DefaultRequestHeaders</c>) so two concurrent flow.map branches can never bleed one team's key onto another's
/// request. The success body is read via <see cref="HttpContentJsonExtensions.ReadFromJsonAsync"/> (no UTF-16
/// double-buffer); the error body is read as a string only on the non-2xx path.</para>
/// </summary>
internal static class LlmHttpTransport
{
    public static async Task<T> SendForJsonAsync<T>(HttpClient http, HttpRequestMessage request, string provider, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A CLIENT-SIDE timeout (HttpClient.Timeout / a per-call budget) fired — NOT an operator/run cancel (that
            // would have IsCancellationRequested == true and is re-thrown to abort the run). A timeout produced no
            // billable completion, so it is a Transient fault the engine RetryPlan may re-attempt.
            throw new LlmApiException(provider, null, LlmErrorCategory.Transient, "the request timed out before the gateway responded");
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / reset / DNS — the gateway was unreachable. No completion produced → Transient.
            throw new LlmApiException(provider, null, LlmErrorCategory.Transient, ex.Message, inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                throw new LlmApiException(provider, status, LlmApiException.Classify(status, errorBody), errorBody, RetryAfterOf(response));
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(options, cancellationToken).ConfigureAwait(false)
                    ?? throw new LlmApiException(provider, (int)response.StatusCode, LlmErrorCategory.Malformed, $"{provider} API returned an empty body.");
            }
            catch (JsonException ex)
            {
                throw new LlmApiException(provider, (int)response.StatusCode, LlmErrorCategory.Malformed, $"{provider} API returned an unparseable body: {ex.Message}", inner: ex);
            }
        }
    }

    /// <summary>
    /// Stream a Server-Sent-Events response, yielding each <c>data:</c> line's parsed JSON event for the caller to
    /// accumulate — the shared streaming mechanic behind large / unbounded completions (a non-streaming request whose
    /// output can exceed the threshold risks an idle-connection timeout; streaming sends bytes continuously to keep it
    /// alive). Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> so parsing begins as headers arrive, and turns
    /// the SAME failure shapes into the SAME typed <see cref="LlmApiException"/> as <see cref="SendForJsonAsync{T}"/>
    /// (timeout/transport → Transient; a non-2xx before the stream → the classified status). A malformed/partial
    /// <c>data:</c> line is skipped (robust), and OpenAI's <c>[DONE]</c> sentinel + non-<c>data:</c> lines are ignored.
    /// </summary>
    public static async IAsyncEnumerable<JsonElement> StreamSseAsync(HttpClient http, HttpRequestMessage request, string provider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmApiException(provider, null, LlmErrorCategory.Transient, "the request timed out before the gateway responded");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmApiException(provider, null, LlmErrorCategory.Transient, ex.Message, inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                throw new LlmApiException(provider, status, LlmApiException.Classify(status, errorBody), errorBody, RetryAfterOf(response));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new System.IO.StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // skip event: / id: / comment / blank lines

                var data = line[5..].TrimStart();
                if (data.Length == 0 || data == "[DONE]") continue;                  // OpenAI's terminal sentinel

                JsonElement? evt = null;
                try { using var doc = JsonDocument.Parse(data); evt = doc.RootElement.Clone(); }
                catch (JsonException) { /* a partial/malformed data line — skip, keep reading */ }

                if (evt is { } e) yield return e;
            }
        }
    }

    /// <summary>A BUFFERED <c>application/json</c> body (bare content-type — no <c>; charset=utf-8</c> suffix that some strict OpenAI-compatible gateways reject). Buffered (a serialized string) so the resilience handler can re-send it across a transient retry — a streaming <c>JsonContent</c> could not be re-read.</summary>
    public static StringContent JsonBody<T>(T body, JsonSerializerOptions? options = null)
    {
        var content = new StringContent(JsonSerializer.Serialize(body, options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }

    /// <summary>The provider's <c>Retry-After</c> as a delay — a delta header verbatim, or an absolute date converted to a delay from now (clamped non-negative); null when absent.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;

        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date) { var d = date - DateTimeOffset.UtcNow; return d > TimeSpan.Zero ? d : TimeSpan.Zero; }

        return null;
    }
}
