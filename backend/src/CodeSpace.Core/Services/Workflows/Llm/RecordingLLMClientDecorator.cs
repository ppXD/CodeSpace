using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Constants;
using Serilog;

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
///
/// <para><b>It is also the producer of the <see cref="WorkflowRunDataOwnerKinds.ModelCall"/> completeness facet</b>, in
/// the order that makes a lost accounting fail closed: the ONE record this call owes is DECLARED before the first
/// ledger row and stated present only once a terminal row is durable, so the window between them reads present below
/// expected. The mirror — a lost DECLARATION — does not fail closed on its own, because migration 0171 seeds this facet
/// at a determinate expected=0 and a presence stated alone would land present=1 over it, which 0148 reads as Exact over
/// a model call whose obligation nobody established. So a declaration that is refused OR that throws un-states the
/// facet's expectation instead, and this call states no presence at all. Every completeness write is contained here as
/// well as in the writer: capture accounting may not fault a model call in either direction.</para>
/// </summary>
public class RecordingLLMClientDecorator : ILLMClient
{
    private readonly ILLMClient _inner;

    public RecordingLLMClientDecorator(ILLMClient inner) { _inner = inner; }

    public string Provider => _inner.Provider;

    public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current?.ForOneCall();
        if (scope is null) return await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        var declared = await DeclareCaptureIntentAsync(scope).ConfigureAwait(false);
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
            var failureRecorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), CancellationToken.None).ConfigureAwait(false);
            if (failureRecorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);

            throw;
        }

        var recorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, completion.Model, completion.Usage, await OffloadTextAsync(scope, completion.Text, CancellationToken.None).ConfigureAwait(false)), CancellationToken.None).ConfigureAwait(false);
        if (recorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);

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

    /// <summary>
    /// What this call UNDERTAKES to capture — ONE model-call record — stated BEFORE the first ledger row, so an
    /// accounting lost after the rows land leaves present below expected rather than two counts that fell short
    /// together. Returns whether the statement was ADMITTED, and that answer is load-bearing: a lost declaration may
    /// never be followed by a present-only advance, which would manufacture Exact over the determinate expected=0 that
    /// 0171 seeds this facet at. A refused declaration and a thrown one are the same lost declaration, so both un-state
    /// the facet's expectation here — indeterminate, which 0146 refuses every complete verdict over. A scope carrying
    /// no completeness writer states nothing at all and reports the same false, which keeps it on the one path that
    /// also presents nothing.
    /// </summary>
    protected static async Task<bool> DeclareCaptureIntentAsync(LlmCallScope scope)
    {
        if (scope.Completeness is not { } writer) return false;

        if (await AdmittedAsync(writer, ModelCallAdvance(scope, expected: 1, present: 0, masked: false)).ConfigureAwait(false)) return true;

        Log.Warning("The capture-intent declaration for a model call of workflow run {WorkflowRunId} was not admitted, so the {Facet} facet's expectation is un-stated rather than counted from a present-only delta; the call and its ledger rows are untouched", scope.RunId, WorkflowRunDataOwnerKinds.ModelCall);
        await UnstateModelCallExpectationAsync(writer, scope).ConfigureAwait(false);

        return false;
    }

    /// <summary>The record this call owed is durable, so the facet's presence advances by the one it declared — carrying whether THIS call's capture actually replaced content, which is the question 0166's latch asks and the one a configured redactor cannot answer.</summary>
    protected static async Task MarkCapturePresentAsync(LlmCallScope scope)
    {
        if (scope.Completeness is not { } writer) return;

        await AdmittedAsync(writer, ModelCallAdvance(scope, expected: 0, present: 1, masked: scope.Masking?.Observed ?? false)).ConfigureAwait(false);
    }

    /// <summary>One fold offered to the writer, contained here as well as there. The writer reports a refusal as false rather than throwing, and a decorator may not fault a model call over one that does — but a thrown claim is a LOST claim, never an admitted one, which is exactly the difference a swallowed exception used to hide.</summary>
    private static async Task<bool> AdmittedAsync(IRunDataCompletenessWriter writer, RunDataFacetAdvance advance)
    {
        try
        {
            return await writer.AdvanceAsync(advance, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "The {Facet} completeness statement of workflow run {WorkflowRunId} threw and is lost; the model call and its ledger rows are untouched", advance.Facet, advance.WorkflowRunId);

            return false;
        }
    }

    /// <summary>The facet's expectation stops being knowable, contained for the same reason: an un-stating that itself fails leaves whatever expectation the facet already carried, and this call states no presence over it either way.</summary>
    private static async Task UnstateModelCallExpectationAsync(IRunDataCompletenessWriter writer, LlmCallScope scope)
    {
        try
        {
            await writer.UnstateExpectationAsync(scope.TeamId, scope.RunId, WorkflowRunDataOwnerKinds.ModelCall, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Un-stating the {Facet} expectation of workflow run {WorkflowRunId} threw; the facet keeps the expectation it already carried and this call states no presence", WorkflowRunDataOwnerKinds.ModelCall, scope.RunId);
        }
    }

    /// <summary>One delta this call may state about the model-call facet — the two counts stay separate because a producer that advanced both together would leave them equally short whenever an accounting was lost.</summary>
    private static RunDataFacetAdvance ModelCallAdvance(LlmCallScope scope, long expected, long present, bool masked) => new()
    {
        TeamId = scope.TeamId, WorkflowRunId = scope.RunId, Facet = WorkflowRunDataOwnerKinds.ModelCall,
        Expected = expected, Present = present, Masked = masked,
    };

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

        var error = scope.CaptureRedactor is null ? ex.Message : Observe(scope, scope.CaptureRedactor.Redact(ex.Message));
        return JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, error, category, failureKind });
    }

    /// <summary>Fold one redaction into THIS call's masking observation and hand its value back, so the flag the presence delta carries is a fact about the bytes that reached storage rather than about the configuration. A scope minted outside <see cref="LlmCallScope.ForOneCall"/> observes nothing and reads back verbatim, which is the conservative answer.</summary>
    private static T Observe<T>(LlmCallScope scope, PersistenceRedaction<T> redaction) => scope.Masking is { } masking ? masking.Observe(redaction) : redaction.Value;

    /// <summary>A plain-text field (a prompt / a text completion): the inline string when small, else a content-addressed <c>$artifact_id</c> ref. Null/empty rides as-is.</summary>
    protected static async Task<object?> OffloadTextAsync(LlmCallScope scope, string? text, CancellationToken cancellationToken)
    {
        if (scope.CaptureRedactor is not null) text = Observe(scope, scope.CaptureRedactor.Redact(text));
        if (string.IsNullOrEmpty(text)) return text;

        var off = await scope.Offloader.OffloadIfLargeAsync(scope.TeamId, text, "text/plain", cancellationToken).ConfigureAwait(false);

        return off.ArtifactId is { } id ? ArtifactRef(id, Encoding.UTF8.GetByteCount(text), "text/plain") : off.Inline;
    }

    /// <summary>A JSON field (a structured completion): the inline JSON object when small, else a <c>$artifact_id</c> ref to its serialized bytes.</summary>
    protected static async Task<object?> OffloadJsonAsync(LlmCallScope scope, JsonElement json, CancellationToken cancellationToken)
    {
        if (scope.CaptureRedactor is not null) json = Observe(scope, scope.CaptureRedactor.Redact(json));
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
