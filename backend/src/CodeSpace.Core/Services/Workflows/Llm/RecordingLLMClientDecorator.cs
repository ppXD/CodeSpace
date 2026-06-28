using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Records EVERY in-process model call onto the run-record ledger as a correlated <c>interaction.*</c> triple — the
/// prompt + params on <c>interaction.started</c>, the raw completion + usage on <c>interaction.completed</c>, the error
/// on <c>interaction.failed</c> — WITHOUT the caller (the supervisor decider, a planner, an llm.complete node) writing
/// any capture code: a pure side-channel decorator over the LLM client seam (Autofac <c>RegisterDecorator</c>).
///
/// <para>It decorates <see cref="ILLMClient"/> — the interface the <c>LLMClientRegistry</c> holds and that the
/// supervisor decider resolves then casts to <see cref="IStructuredLLMClient"/> — and implements BOTH so that cast
/// lands on the decorator (every real provider implements both; <see cref="CompleteStructuredAsync"/> delegates to the
/// inner's structured face). It is registered over a SINGLETON client, so it holds no per-run state — it reads the
/// run/node/turn identity AND the scoped ledger writer + artifact offloader off the ambient <see cref="LlmCallContext"/>
/// a scoped caller pushed (absent ⇒ a call outside any run ⇒ records nothing). FAIL-OPEN by contract: the inner result
/// is always returned/thrown verbatim, and a capture write that fails can never fault the model call or the run. Big
/// prompts/completions offload to content-addressed (sha-deduped) artifacts; the row keeps a small <c>$artifact_id</c> ref.</para>
/// </summary>
public sealed class RecordingLLMClientDecorator : ILLMClient, IStructuredLLMClient
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

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        if (_inner is not IStructuredLLMClient structuredInner)
            throw new NotSupportedException($"LLM provider '{Provider}' does not implement structured completion.");

        var scope = LlmCallContext.Current;
        if (scope is null) return await structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        StructuredLLMCompletion completion;
        try
        {
            completion = await structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, completion.Model, completion.Usage, await OffloadJsonAsync(scope, completion.Json, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        return completion;
    }

    /// <summary>Build the payload + write the row, swallowing ANY failure — capturing an interaction must never fault the model call or the run (a ledger/artifact write error, or a cancellation, is best-effort lost, never propagated).</summary>
    private static async Task SafeRecordAsync(LlmCallScope scope, string recordType, Guid correlationId, Func<Task<JsonElement>> buildPayload, CancellationToken cancellationToken)
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

    private static async Task<JsonElement> StartedPayloadAsync(LlmCallScope scope, string provider, string model, string system, string user, double temperature, int maxOutputTokens, CancellationToken cancellationToken)
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

    private static JsonElement CompletedPayload(LlmCallScope scope, string provider, string model, LlmUsage usage, object? output) =>
        JsonSerializer.SerializeToElement(new
        {
            kind = scope.Kind,
            provider,
            model,
            usage = new { inputTokens = usage.InputTokens, outputTokens = usage.OutputTokens, finishReason = usage.FinishReason },
            output,
        });

    private static JsonElement FailedPayload(LlmCallScope scope, string provider, Exception ex)
    {
        var category = ex is LlmApiException llm ? llm.Category.ToString() : null;
        return JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, error = ex.Message, category });
    }

    /// <summary>A plain-text field (a prompt / a text completion): the inline string when small, else a content-addressed <c>$artifact_id</c> ref. Null/empty rides as-is.</summary>
    private static async Task<object?> OffloadTextAsync(LlmCallScope scope, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var off = await scope.Offloader.OffloadIfLargeAsync(scope.TeamId, text, "text/plain", cancellationToken).ConfigureAwait(false);

        return off.ArtifactId is { } id ? ArtifactRef(id, Encoding.UTF8.GetByteCount(text), "text/plain") : off.Inline;
    }

    /// <summary>A JSON field (a structured completion): the inline JSON object when small, else a <c>$artifact_id</c> ref to its serialized bytes.</summary>
    private static async Task<object?> OffloadJsonAsync(LlmCallScope scope, JsonElement json, CancellationToken cancellationToken)
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
