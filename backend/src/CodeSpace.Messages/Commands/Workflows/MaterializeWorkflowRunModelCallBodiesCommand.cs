using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Bounded leased materialization of already-declared Workflow Run model-call telemetry bodies.</summary>
public sealed record MaterializeWorkflowRunModelCallBodiesCommand : ICommand<int>
{
    public int BatchSize { get; init; } = 100;
}
