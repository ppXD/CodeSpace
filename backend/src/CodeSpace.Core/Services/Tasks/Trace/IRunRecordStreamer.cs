using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Core.Services.Tasks.Trace;

/// <summary>
/// The LIVE TAIL of a run's append-only ledger — the streaming counterpart of <see cref="IRunRecordReader"/> (which
/// returns a one-shot snapshot). Yields every <see cref="RunRecordView"/> whose Sequence is beyond the caller's cursor
/// AS it lands, so a consumer (the Room's SSE relay) sees interaction.delta / lifecycle rows live instead of re-polling
/// the whole ledger. Team-scoped (fail-closed on a foreign / absent run → yields nothing). Ends when the run reaches a
/// terminal <c>run.*</c> record, or the caller disconnects (cancellation). Additive + read-only — no schema, no mutation.
/// </summary>
public interface IRunRecordStreamer : IScopedDependency
{
    IAsyncEnumerable<RunRecordView> TailAsync(Guid runId, long afterSequence, CancellationToken cancellationToken);
}
