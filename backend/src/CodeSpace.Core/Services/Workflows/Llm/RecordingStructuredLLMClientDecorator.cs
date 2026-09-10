using CodeSpace.Messages.Constants;
using Serilog;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The structured-capable sibling of <see cref="RecordingLLMClientDecorator"/> — applied (by conditional registration)
/// ONLY to a client that ALSO implements <see cref="IStructuredLLMClient"/>, so it implements both faces and the
/// decorated type accurately mirrors the inner's. This is the face the supervisor decider / planner reach when they
/// resolve a structured client (<c>registry.All.OfType&lt;IStructuredLLMClient&gt;()</c>) — the cast lands here, and the
/// structured call is recorded the same generic way as the plain one. It inherits the plain-text recording (and all the
/// fail-open capture/offload machinery) from the base and adds only the structured-call recording.
///
/// <para>Splitting the two (rather than one decorator implementing both unconditionally) is what keeps the type honest:
/// a plain-text-only client stays non-structured after wrapping, so a consumer feature-detecting with
/// <c>is not IStructuredLLMClient</c> (the merge synthesis picking a dedicated text provider) is never fooled into the
/// fallback, and the decider never matches a non-structured client as if it were structured. Not sealed so
/// <see cref="RecordingStreamingStructuredLLMClientDecorator"/> can extend it for a structured+streaming client.</para>
/// </summary>
public class RecordingStructuredLLMClientDecorator : RecordingLLMClientDecorator, IStructuredLLMClient
{
    private readonly IStructuredLLMClient _structuredInner;

    public RecordingStructuredLLMClientDecorator(ILLMClient inner) : base(inner) => _structuredInner = (IStructuredLLMClient)inner;

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current?.ForOneCall();
        if (scope is null)
        {
            Log.Warning("Structured model call to provider {Provider} model {Model} has no LlmCallContext at all (AsyncLocal did not flow to this call site) — proceeding UNMETERED and UNRECORDED; the budget guard cannot see a call it has no scope for", Provider, request.Model);
            return await _structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var native = scope is { Budget: not null, CapUsd: not null } && _structuredInner is IPhysicalStructuredLLMClient;
        using var operation = native ? PhysicalLlmCallContext.EnterOperation() : null;
        using var candidate = native ? PhysicalLlmCallContext.EnterCandidate(scope, Provider, request) : null;
        if (native) scope = scope with { NativeModelCallId = PhysicalLlmCallContext.LogicalCallId, NativeCredentialRedactor = PhysicalLlmCallContext.CredentialRedactor };
        var correlationId = native ? PhysicalLlmCallContext.CandidateId!.Value : Guid.NewGuid();
        var declared = await DeclareCaptureIntentAsync(scope).ConfigureAwait(false);
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        StructuredLLMCompletion completion;
        try
        {
            // W-hard: same guard as the plain face — the decider's structured decision calls are exactly the
            // brain-plane spend the cap must bound atomically.
            completion = native
                ? PhysicalLlmCallContext.Aggregate(await _structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false), candidateOnly: true)
                : await LlmBudgetGuard.GuardedAsync(scope, request.Model, request.SystemPrompt, request.UserPrompt, request.MaxOutputTokens,
                ct => _structuredInner.CompleteStructuredAsync(request, ct),
                c => Agents.Cost.LlmUsageCost.Usd(c.Model, c.Usage, scope.ModelPrices),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failureRecorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), CancellationToken.None).ConfigureAwait(false);
            if (failureRecorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);

            throw;
        }

        var recorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, completion.Model, completion.Usage, await OffloadJsonAsync(scope, completion.Json, CancellationToken.None).ConfigureAwait(false)), CancellationToken.None).ConfigureAwait(false);
        if (recorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);

        return completion;
    }
}
