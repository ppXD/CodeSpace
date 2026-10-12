using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
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
/// As the text flows it ALSO appends bounded COALESCED <c>interaction.delta</c> rows: at 32 KiB, or at the one-second
/// live-view cadence for a slow stream — never per token. Each carries a monotonic ordinal and the same correlation id;
/// deltas remain a progressive VIEW while <c>completed</c> stays the whole-output source of truth (replay ignores
/// deltas). Fail-open, inherited from the base: a capture write can never fault the model call or the stream.</para>
/// </summary>
public sealed class RecordingStreamingStructuredLLMClientDecorator : RecordingStructuredLLMClientDecorator, IStreamingLLMClient
{
    /// <summary>Size bound for one progressive write. UTF-8 bytes, not UTF-16 chars, so multilingual output gets the same storage/transaction bound as ASCII.</summary>
    internal const int DeltaFlushBytes = 32 * 1024;

    /// <summary>A sub-threshold fragment is visible within this cadence while the provider remains open.</summary>
    internal static readonly TimeSpan DeltaFlushInterval = TimeSpan.FromSeconds(1);

    private readonly IStreamingLLMClient _streamingInner;
    private readonly TimeProvider _timeProvider;

    public RecordingStreamingStructuredLLMClientDecorator(ILLMClient inner) : this(inner, TimeProvider.System) { }

    public RecordingStreamingStructuredLLMClientDecorator(ILLMClient inner, TimeProvider timeProvider) : base(inner)
    {
        _streamingInner = (IStreamingLLMClient)inner;
        _timeProvider = timeProvider;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scope = LlmCallContext.Current?.ForOneCall();
        if (scope is null)
        {
            await foreach (var e in _streamingInner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                yield return e;
            yield break;
        }

        var correlationId = Guid.NewGuid();
        var declared = await DeclareCaptureIntentAsync(scope).ConfigureAwait(false);
        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            () => StartedPayloadAsync(scope, Provider, request.Model, request.SystemPrompt, request.UserPrompt, request.Temperature, request.MaxOutputTokens, cancellationToken), cancellationToken).ConfigureAwait(false);

        var accumulator = new LlmCompletionAccumulator();
        var pending = new PendingDelta(PersistenceSecretRedactor.Union(scope.NativeCredentialRedactor, scope.CaptureRedactor));

        await using var enumerator = _streamingInner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        using var timer = new PeriodicTimer(DeltaFlushInterval, _timeProvider);
        var tickTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        while (true)
        {
            bool moved;
            try
            {
                if (!pending.IsEmpty)
                {
                    var completed = await Task.WhenAny(moveNextTask, tickTask).ConfigureAwait(false);
                    if (completed == tickTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!await tickTask.ConfigureAwait(false)) break;

                        await RecordPendingDeltaAsync(scope, correlationId, pending, DeltaFlush.Settled, cancellationToken).ConfigureAwait(false);
                        tickTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                        continue;
                    }
                }

                moved = await moveNextTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The caller's token is commonly already cancelled here. Capture uses its own non-cancelled attempt so
                // cancellation itself remains a typed terminal fact; SafeRecord still swallows any ledger failure.
                if (!pending.IsEmpty)
                    await RecordPendingDeltaAsync(scope, correlationId, pending, DeltaFlush.Cut, CancellationToken.None).ConfigureAwait(false);

                var failureRecorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionFailed, correlationId, () => Task.FromResult(FailedPayload(scope, Provider, ex)), CancellationToken.None).ConfigureAwait(false);
                if (failureRecorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);

                throw;
            }

            if (!moved) break;

            var current = enumerator.Current;
            accumulator.Add(current);

            if (current is LlmStreamEvent.TextDelta delta)
            {
                pending.Append(delta.Text);
                if (pending.Bytes >= DeltaFlushBytes)
                    await RecordPendingDeltaAsync(scope, correlationId, pending, DeltaFlush.Settled, cancellationToken).ConfigureAwait(false);
            }

            yield return current;
            moveNextTask = enumerator.MoveNextAsync().AsTask();
        }

        // Keep the final tail in the ordered live projection. This is at most one extra row per streamed call, preserves
        // exact concatenation, and does not change authority: completed below remains the canonical whole output.
        if (!pending.IsEmpty)
            await RecordPendingDeltaAsync(scope, correlationId, pending, DeltaFlush.End, cancellationToken).ConfigureAwait(false);

        var recorded = await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            async () => CompletedPayload(scope, Provider, accumulator.ResolveModel(request.Model), accumulator.Usage, await OffloadTextAsync(scope, accumulator.Text, CancellationToken.None).ConfigureAwait(false)), CancellationToken.None).ConfigureAwait(false);
        if (recorded && declared) await MarkCapturePresentAsync(scope).ConfigureAwait(false);
    }

    private async Task RecordPendingDeltaAsync(LlmCallScope scope, Guid correlationId, PendingDelta pending, DeltaFlush flush, CancellationToken cancellationToken)
    {
        if (pending.Take(flush) is not { } fragment) return;

        var text = Observe(scope, fragment.Text);

        await SafeRecordAsync(scope, WorkflowRunRecordTypes.InteractionDelta, correlationId,
            async () => DeltaPayload(scope, Provider, fragment.Ordinal, await OffloadTextAsync(scope, text, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a flush may release of the held text.</summary>
    private enum DeltaFlush
    {
        /// <summary>Mid-stream: only the settled text; the rest is carried into the next flush.</summary>
        Settled,

        /// <summary>The stream ended: everything, verbatim — a held tail nothing followed never became a secret.</summary>
        End,

        /// <summary>
        /// The stream was cut — a cancel or a fault: everything, with a held tail that could start a secret masked. The
        /// carry holds exactly that text, the rest of it was never streamed, and exact matching never sees it whole.
        /// </summary>
        Cut,
    }

    /// <summary>
    /// The text streamed since the last delta row, and the ordinal the next row takes. Each row is redacted on its own, so
    /// a flush mid-stream releases only the SETTLED text — none that a secret could still run across — and carries the
    /// rest into the next flush; otherwise a secret the model prints across a flush (by size or by the one-second
    /// cadence) is stored as two halves no exact match recognises, and the live view concatenates them back. The end
    /// of the stream releases everything, so the rows still concatenate to exactly the streamed text, masked; a cut
    /// releases everything too, but masks the end it was holding back.
    /// </summary>
    private sealed class PendingDelta(PersistenceSecretRedactor boundary)
    {
        private readonly StringBuilder _text = new();
        private int _ordinal;

        /// <summary>UTF-8 size of the held text — the size bound is in bytes, not UTF-16 chars.</summary>
        public int Bytes { get; private set; }

        public bool IsEmpty => _text.Length == 0;

        public void Append(string text)
        {
            _text.Append(text);
            Bytes += Encoding.UTF8.GetByteCount(text);
        }

        /// <summary>The text one row may carry and that row's ordinal, or null when nothing is settled yet.</summary>
        public (PersistenceRedaction<string?> Text, int Ordinal)? Take(DeltaFlush flush)
        {
            var text = _text.ToString();
            var length = flush == DeltaFlush.Settled ? boundary.SettledLength(text) : text.Length;

            if (length == 0) return null;

            _text.Remove(0, length);
            Bytes = Encoding.UTF8.GetByteCount(text.AsSpan(length));

            var released = flush == DeltaFlush.Cut ? boundary.RedactCut(text) : new PersistenceRedaction<string?>(text[..length], false);

            return (released, _ordinal++);
        }
    }

    /// <summary>The <c>interaction.delta</c> payload: the ordinal (monotonic within the call) + the coalesced text fragment (inline-or-<c>$artifact_id</c>).</summary>
    private static JsonElement DeltaPayload(LlmCallScope scope, string provider, int ordinal, object? text) =>
        JsonSerializer.SerializeToElement(new { kind = scope.Kind, provider, ordinal, text });
}
