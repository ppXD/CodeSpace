using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
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
        await DeclareCaptureIntentAsync(scope).ConfigureAwait(false);
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        LLMCompletion completion;
        try
        {
            // W-hard: the budget guard rides INSIDE the recording pair, so a cap refusal lands on the tape as this
            // call's Failed row — legible, never a silent skip. A scope without a ledger+cap passes through.
            completion = await LlmBudgetGuard.GuardedAsync(scope, request.Model, request.SystemPrompt, request.UserPrompt, request.MaxOutputTokens,
                ct => _inner.CompleteAsync(request, ct),
                c => Agents.Cost.AgentCostPricing.CostUsd(c.Model, c.Usage.InputTokens ?? 0, c.Usage.OutputTokens ?? 0),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The caller token is commonly cancelled by the failure itself. Terminal capture gets one independent,
            // fail-open attempt so cancellation does not erase the interaction's final fact.
            if (await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), CancellationToken.None).ConfigureAwait(false))
                await MarkCapturePresentAsync(scope).ConfigureAwait(false);
            throw;
        }

        if (await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, completion.Model, completion.Usage, await OffloadTextAsync(scope, completion.Text, CancellationToken.None).ConfigureAwait(false)), CancellationToken.None).ConfigureAwait(false))
            await MarkCapturePresentAsync(scope).ConfigureAwait(false);
        return completion;
    }

    /// <summary>Build the payload + write the row, swallowing ANY failure — capturing an interaction must never fault the model call or the run (a ledger/artifact write error, or a cancellation, is best-effort lost, never propagated).</summary>
    protected static async Task<bool> SafeRecordAsync(LlmCallScope scope, string recordType, Guid correlationId, Func<Task<JsonElement>> buildPayload, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await buildPayload().ConfigureAwait(false);
            await scope.Logger.RecordInteractionAsync(scope.RunId, recordType, scope.NodeId, scope.IterationKey, correlationId, parentRecordId: null, payload, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (scope.Completeness is not null)
            {
                try
                {
                    await scope.Completeness.NoticeAsync(new WorkflowRunCaptureGap
                    {
                        Id = Guid.NewGuid(), TeamId = scope.TeamId, WorkflowRunId = scope.RunId,
                        SubjectKind = WorkflowRunDataOwnerKinds.ModelCall, SubjectId = $"{correlationId:N}/{recordType}",
                        RangeKind = CaptureGapRangeKind.Unbounded, Reason = CaptureGapReason.WriteRefused,
                        ReasonDetail = $"{recordType} capture failed with {ex.GetType().Name}.", CaptureSource = "in-process",
                        NoticedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            return false;
        }
    }

    protected static async Task DeclareCaptureIntentAsync(LlmCallScope scope)
    {
        if (scope.Completeness is null) return;
        try
        {
            await scope.Completeness.AdvanceAsync(new RunDataFacetAdvance
            {
                TeamId = scope.TeamId, WorkflowRunId = scope.RunId, Facet = WorkflowRunDataOwnerKinds.ModelCall,
                Expected = 1, Present = 0, Masked = false,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    protected static async Task MarkCapturePresentAsync(LlmCallScope scope)
    {
        if (scope.Completeness is null) return;
        try
        {
            await scope.Completeness.AdvanceAsync(new RunDataFacetAdvance
            {
                TeamId = scope.TeamId, WorkflowRunId = scope.RunId, Facet = WorkflowRunDataOwnerKinds.ModelCall,
                Expected = 0, Present = 1, Masked = scope.CaptureRedactor is not null,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    protected static async Task<JsonElement> StartedPayloadAsync(LlmCallScope scope, string provider, string model, string system, string user, double? temperature, int? maxOutputTokens, CancellationToken cancellationToken)
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
        var failureKind = ex switch
        {
            OperationCanceledException => "cancelled",
            LlmApiException => "provider",
            _ => "exception",
        };

        var error = scope.CaptureRedactor is null ? ex.Message : scope.CaptureRedactor.Redact(ex.Message).Value;
        return JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, error, category, failureKind });
    }

    /// <summary>A plain-text field (a prompt / a text completion): the inline string when small, else a content-addressed <c>$artifact_id</c> ref. Null/empty rides as-is.</summary>
    protected static async Task<object?> OffloadTextAsync(LlmCallScope scope, string? text, CancellationToken cancellationToken)
    {
        if (scope.CaptureRedactor is not null) text = scope.CaptureRedactor.Redact(text).Value;
        if (string.IsNullOrEmpty(text)) return text;

        var off = await scope.Offloader.OffloadIfLargeAsync(scope.TeamId, text, "text/plain", cancellationToken).ConfigureAwait(false);

        return off.ArtifactId is { } id ? ArtifactRef(id, Encoding.UTF8.GetByteCount(text), "text/plain") : off.Inline;
    }

    /// <summary>A JSON field (a structured completion): the inline JSON object when small, else a <c>$artifact_id</c> ref to its serialized bytes.</summary>
    protected static async Task<object?> OffloadJsonAsync(LlmCallScope scope, JsonElement json, CancellationToken cancellationToken)
    {
        if (scope.CaptureRedactor is not null) json = scope.CaptureRedactor.Redact(json).Value;
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
