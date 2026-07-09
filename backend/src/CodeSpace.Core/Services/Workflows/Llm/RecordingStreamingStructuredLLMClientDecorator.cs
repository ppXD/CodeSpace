using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The streaming-capable sibling of <see cref="RecordingStructuredLLMClientDecorator"/> — applied (by conditional
/// registration) ONLY to a client that implements BOTH <see cref="IStructuredLLMClient"/> AND
/// <see cref="IStreamingLLMClient"/>, so it carries all three faces and the decorated type mirrors the inner's. This is
/// what a streaming caller reaches when it resolves a client (<c>registry.All.OfType&lt;IStreamingLLMClient&gt;()</c>) —
/// the cast lands HERE, so a streamed call is captured onto the ledger the SAME generic way a buffered one is, never
/// bypassing the recorder (the seam bug the audit flagged: separate one-face decorators leave the cast landing on
/// nothing, or on the raw client with capture bypassed).
///
/// <para>It TEES: each <see cref="LlmStreamEvent"/> flows to the caller live while a <see cref="LlmCompletionAccumulator"/>
/// folds it, so the SAME <c>interaction.started</c> + <c>interaction.completed</c> (or <c>interaction.failed</c> on a
/// mid-stream fault) triple lands as for a buffered call — the whole-completion row carries the folded text + usage.
/// As the text flows it ALSO appends COALESCED <c>interaction.delta</c> rows (every <see cref="DeltaFlushChars"/> chars,
/// never per token — so a huge stream can't flood the ledger), each with a monotonic ordinal and the same correlation
/// id; the sub-threshold tail rides in the authoritative <c>completed</c>, so deltas are a progressive VIEW and
/// <c>completed</c> stays the whole-output source of truth (replay ignores deltas). Fail-open, inherited from the base:
/// a capture write can never fault the model call or the stream.</para>
/// </summary>
public sealed class RecordingStreamingStructuredLLMClientDecorator : RecordingStructuredLLMClientDecorator, IStreamingLLMClient
{
    /// <summary>Coalescing bound: flush an <c>interaction.delta</c> once the un-flushed text reaches this many chars — bounds the delta-row count on a large stream (a 128K-token output becomes hundreds of rows, not 100k), while staying granular enough to drive a live view.</summary>
    internal const int DeltaFlushChars = 256;

    private readonly IStreamingLLMClient _streamingInner;

    public RecordingStreamingStructuredLLMClientDecorator(ILLMClient inner) : base(inner) => _streamingInner = (IStreamingLLMClient)inner;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current;
        if (scope is null)
        {
            await foreach (var e in _streamingInner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                yield return e;
            yield break;
        }

        var correlationId = Guid.NewGuid();
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        var accumulator = new LlmCompletionAccumulator();
        var pending = new StringBuilder();
        var deltaOrdinal = 0;

        await using var enumerator = _streamingInner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            LlmStreamEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                current = enumerator.Current;
            }
            catch (Exception ex)
            {
                await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), cancellationToken).ConfigureAwait(false);
                throw;
            }

            accumulator.Add(current);

            if (current is LlmStreamEvent.TextDelta delta)
            {
                pending.Append(delta.Text);
                if (pending.Length >= DeltaFlushChars)
                {
                    var fragment = pending.ToString();
                    pending.Clear();
                    var ordinal = deltaOrdinal++;
                    await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionDelta, correlationId,
                        async () => DeltaPayload(scope, Provider, ordinal, await OffloadTextAsync(scope, fragment, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                }
            }

            yield return current;
        }

        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, accumulator.ResolveModel(request.Model), accumulator.Usage, await OffloadTextAsync(scope, accumulator.Text, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>interaction.delta</c> payload: the ordinal (monotonic within the call) + the coalesced text fragment (inline-or-<c>$artifact_id</c>).</summary>
    private static JsonElement DeltaPayload(LlmCallScope scope, string provider, int ordinal, object? text) =>
        JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, ordinal, text });
}
