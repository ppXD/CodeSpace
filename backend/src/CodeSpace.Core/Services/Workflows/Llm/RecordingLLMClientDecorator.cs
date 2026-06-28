using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Records EVERY in-process model call onto the run-record ledger as a correlated <c>interaction.*</c> triple — the
/// prompt + params on <c>interaction.started</c>, the raw completion + usage on <c>interaction.completed</c>, the error
/// on <c>interaction.failed</c> — WITHOUT the caller (a planner, an llm.complete node, the merge synthesis) writing any
/// capture code: a pure side-channel decorator over the LLM client seam (Autofac <c>RegisterDecorator</c>).
///
/// <para>This base decorates the NARROW <see cref="ILLMClient"/> (plain text) face — it is applied to a plain-text-only
/// client (one that does NOT also implement <see cref="IStructuredLLMClient"/>), so the decorated client stays
/// accurately non-structured and a consumer that feature-detects with <c>is not IStructuredLLMClient</c> (e.g. the merge
/// synthesis picking a dedicated text provider) still sees it correctly. A structured-capable client is wrapped by the
/// sibling <see cref="RecordingStructuredLLMClientDecorator"/> instead (conditional registration), so the decorator's
/// implemented interfaces always mirror the inner's — the type never lies.</para>
///
/// <para>It is registered over a SINGLETON client, so it holds no per-run state — it reads the run/node/turn identity
/// AND the scoped ledger writer + artifact offloader off the ambient <see cref="LlmCallContext"/> a scoped caller
/// pushed (absent ⇒ a call outside any run ⇒ records nothing). FAIL-OPEN by contract: the inner result is always
/// returned/thrown verbatim, and a capture write that fails can never fault the model call or the run. Big
/// prompts/completions offload to content-addressed (sha-deduped) artifacts; the row keeps a small <c>$artifact_id</c> ref.</para>
/// </summary>
public class RecordingLLMClientDecorator : ILLMClient
{
    private readonly ILLMClient _inner;

    public RecordingLLMClientDecorator(ILLMClient inner) { _inner = inner; }

    public string Provider => _inner.Provider;

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current;
        if (scope is null) return await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        LLMCompletion completion;
        try
        {
            completion = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, completion.Model, completion.Usage, await OffloadTextAsync(scope, completion.Text, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        return completion;
    }

    /// <summary>Build the payload + write the row, swallowing ANY failure — capturing an interaction must never fault the model call or the run (a ledger/artifact write error, or a cancellation, is best-effort lost, never propagated).</summary>
    protected static async Task SafeRecordAsync(LlmCallScope scope, string recordType, Guid correlationId, Func<Task<JsonElement>> buildPayload, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await buildPayload().ConfigureAwait(false);
            await scope.Logger.RecordInteractionAsync(scope.RunId, recordType, scope.NodeId, scope.IterationKey, correlationId, parentRecordId: null, payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // fail-open — see the class doc.
        }
    }

    protected static async Task<JsonElement> StartedPayloadAsync(LlmCallScope scope, string provider, string model, string system, string user, double temperature, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var sys = await OffloadTextAsync(scope, system, cancellationToken).ConfigureAwait(false);
        var usr = await OffloadTextAsync(scope, user, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            kind = scope.Kind,
            provider,
            model,
            @params = new { temperature, maxOutputTokens },
            prompt = new { system = sys, user = usr },
        });
    }

    protected static JsonElement CompletedPayload(LlmCallScope scope, string provider, string model, LlmUsage usage, object? output) =>
        JsonSerializer.SerializeToElement(new
        {
            kind = scope.Kind,
            provider,
            model,
            usage = new { inputTokens = usage.InputTokens, outputTokens = usage.OutputTokens, finishReason = usage.FinishReason },
            output,
        });

    protected static JsonElement FailedPayload(LlmCallScope scope, string provider, Exception ex)
    {
        var category = ex is LlmApiException llm ? llm.Category.ToString() : null;
        return JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, error = ex.Message, category });
    }

    /// <summary>A plain-text field (a prompt / a text completion): the inline string when small, else a content-addressed <c>$artifact_id</c> ref. Null/empty rides as-is.</summary>
    protected static async Task<object?> OffloadTextAsync(LlmCallScope scope, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var off = await scope.Offloader.OffloadIfLargeAsync(scope.TeamId, text, "text/plain", cancellationToken).ConfigureAwait(false);

        return off.ArtifactId is { } id ? ArtifactRef(id, Encoding.UTF8.GetByteCount(text), "text/plain") : off.Inline;
    }

    /// <summary>A JSON field (a structured completion): the inline JSON object when small, else a <c>$artifact_id</c> ref to its serialized bytes.</summary>
    protected static async Task<object?> OffloadJsonAsync(LlmCallScope scope, JsonElement json, CancellationToken cancellationToken)
    {
        var text = json.GetRawText();

        var off = await scope.Offloader.OffloadIfLargeAsync(scope.TeamId, text, "application/json", cancellationToken).ConfigureAwait(false);

        return off.ArtifactId is { } id ? ArtifactRef(id, Encoding.UTF8.GetByteCount(text), "application/json") : json;
    }

    private static Dictionary<string, object> ArtifactRef(Guid artifactId, int sizeBytes, string contentType) => new()
    {
        ["$artifact_id"] = artifactId.ToString(),
        ["size_bytes"] = sizeBytes,
        ["content_type"] = contentType,
    };
}
