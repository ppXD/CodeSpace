using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Bounded leased materialization of already-declared Workflow Run model-call telemetry bodies.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each intent is claimed by stamping its own lease fence
/// and attempt count, then materialized independently. One command transaction around the whole tick would let a single
/// bad row undo every other row's work.</para>
/// </summary>
public sealed record MaterializeWorkflowRunModelCallBodiesCommand : ICommand<int>, INonTransactionalCommand
{
    public int BatchSize { get; init; } = 100;
}
