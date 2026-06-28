using CodeSpace.Messages.Constants;

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
/// fallback, and the decider never matches a non-structured client as if it were structured.</para>
/// </summary>
public sealed class RecordingStructuredLLMClientDecorator : RecordingLLMClientDecorator, IStructuredLLMClient
{
    private readonly IStructuredLLMClient _structuredInner;

    public RecordingStructuredLLMClientDecorator(ILLMClient inner) : base(inner) => _structuredInner = (IStructuredLLMClient)inner;

    public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current;
        if (scope is null) return await _structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);

        var correlationId = Guid.NewGuid();
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        StructuredLLMCompletion completion;
        try
        {
            completion = await _structuredInner.CompleteStructuredAsync(request, cancellationToken).ConfigureAwait(false);
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
}
