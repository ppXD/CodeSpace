namespace CodeSpace.Messages.Mediation;

/// <summary>
/// Opts a command OUT of the one-transaction-per-command wrapper. For system sweeps that own their own
/// per-row atomicity — a bounded batch of independent rows, each settled by its own fenced / leased CAS
/// write, each failure caught, logged and retried on the next tick.
///
/// <para>Wrapping such a sweep in a single transaction inverts its design twice over. It makes the batch
/// atomic, so one unrecoverable row undoes every OTHER row's recovery — and a sweep that recovers stuck
/// work is exactly the thing that must not stop working because one item is broken. And it holds every
/// row lock the tick touched until the tick ends, across whatever probing, process inspection or external
/// call the sweep does in between.</para>
///
/// <para>A marked command's handler therefore gets NO ambient transaction and NO framework
/// <c>SaveChanges</c>: the sweep's own writes must be self-committing (raw CAS statements, or a service
/// that saves its own unit of work). Deferred post-commit actions still drain once the handler returns,
/// so a dispatch staged behind a service-owned transaction is not dropped.</para>
///
/// <para>This is not an escape hatch for a command that merely feels slow. A command that writes ONE
/// coherent unit of work — several rows that must land together or not at all — keeps the transaction.</para>
/// </summary>
public interface INonTransactionalCommand
{
}
