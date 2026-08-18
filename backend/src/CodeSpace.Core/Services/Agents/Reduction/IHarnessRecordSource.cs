using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// Where a reduction reads its frames from. It is an abstraction rather than a query against a table because the
/// reduction has to be provable: a source under a test's control can present the exact stream a bug needs — a fact
/// stated once before a checkpoint boundary, a superseded projection, a frame re-delivered after a crash — and the
/// same reduction then runs unchanged over the real capture plane.
///
/// <para><b>The contract, which the source owns and the reducer cannot verify.</b> A source yields frames in a
/// deterministic TOTAL order, stable across calls, whose restriction to any one stream is ascending ordinal order with
/// no gaps. Given <paramref name="after"/>, it yields the SUFFIX of that order — it may re-deliver frames the position
/// already covers (they are skipped), but it may never omit one it does not.</para>
///
/// <para>A source that reorders frames between reads changes the answer every order-dependent reduction gives, and
/// nothing downstream can detect it: two different values are both internally consistent. A persisted source
/// discharges the obligation with a deterministic sort — <c>ORDER BY ingested_at, stream_id, ordinal</c> is one — and
/// never with an unordered read. The reducer does enforce what it CAN see: a frame behind the frontier is skipped, and
/// a frame that jumps past it raises rather than folding a prefix with a hole in it.</para>
/// </summary>
public interface IHarnessRecordSource
{
    /// <summary>The frames strictly after <paramref name="after"/> in this source's reduction order, oldest first.</summary>
    IAsyncEnumerable<HarnessReductionFrame> ReadForwardAsync(HarnessReductionPosition after, CancellationToken cancellationToken);
}
