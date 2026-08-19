using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional sibling capability of <see cref="ISandboxDurableRunner"/> (Rule 7 / ISP — never a widening of it): deliver
/// a launched run's DIAGNOSTIC stream — its stderr — line by line, exactly as
/// <see cref="ISandboxDurableRunner.AttachAsync"/> delivers stdout.
///
/// <para><b>Why a sibling and not a second callback on AttachAsync.</b> A run's stdout is the harness's protocol and
/// its stderr is the harness's own diagnostics; they are read by different consumers for different reasons, and a
/// caller that wants only the protocol must keep exactly the contract it has. Widening the attach signature would also
/// put it past the parameter cap for a capability most runners cannot offer at all.</para>
///
/// <para><b>Retention, stated as what it actually is.</b> Lines are delivered and not accumulated: nothing an
/// implementation holds grows with the run. It is NOT "retains nothing" — a reader materializes whatever pass it is
/// decoding, so peak retention is one pass's worth of bytes and the lines that pass holds. What the seam guarantees is
/// that the peak is a CONSTANT of the implementation, not a function of how much the harness wrote.</para>
///
/// <para><b>The drain is BOUNDED IN BOTH DIMENSIONS, and the caller sets both bounds.</b>
/// <see cref="SandboxDiagnosticBudget"/> is not a hint: a caller that turns each line into durable work pays for every
/// line AND for every byte in it, and a run that emits a hundred megabytes of stderr must not be able to turn a
/// completed round into an unbounded write — which a line budget alone does not prevent, because a hundred megabytes
/// can arrive in ten lines. So the drain stops at whichever budget is reached first and answers where it stopped, and
/// the caller decides what an exhausted budget means.</para>
///
/// <para><b>Progress is guaranteed.</b> The one input that can defeat a line-by-line reader is a line longer than a
/// read pass, which no pass can terminate. An implementation may not answer that by consuming nothing: the caller
/// cannot tell a zero-advance from a finished stream, so the rest of the source would be unreachable through this seam
/// for good while the caller recorded a clean drain. It delivers the cut instead, marked
/// <see cref="SandboxDiagnosticLine.IsComplete"/> false, and the remainder is the next delivery.</para>
///
/// <para><b>Ordering.</b> The drain is for a stream whose producer is gone — the source's final line is delivered even
/// when it carries no terminator, so calling this while the process is still writing would deliver a partial line as a
/// whole one.</para>
/// </summary>
public interface ISandboxDurableDiagnosticSource
{
    /// <summary>
    /// Deliver diagnostic lines from <paramref name="fromOffset"/> — within <paramref name="budget"/>, and no further
    /// than the end of the source — invoking <paramref name="onLine"/> for each, and answer the advanced byte offset:
    /// the position the next drain of the same source resumes at. An exhausted budget is therefore not lost data, it is
    /// a resumable position. A source that does not exist, one with nothing past <paramref name="fromOffset"/>, and a
    /// <paramref name="budget"/> with nothing left in it each deliver nothing and answer <paramref name="fromOffset"/>
    /// unchanged; a call with budget to spend and source to read ALWAYS advances.
    /// </summary>
    Task<long> DrainDiagnosticsAsync(SandboxHandle handle, long fromOffset, SandboxDiagnosticBudget budget, Func<SandboxDiagnosticLine, CancellationToken, Task> onLine, CancellationToken cancellationToken);
}
