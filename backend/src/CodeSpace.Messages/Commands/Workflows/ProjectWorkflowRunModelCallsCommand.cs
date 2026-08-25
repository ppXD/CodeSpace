using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Bounded shadow projection of started and terminal interaction facts into first-class Workflow Run model-call rows.</summary>
public sealed record ProjectWorkflowRunModelCallsCommand : ICommand<int>
{
    public int BatchSize { get; init; } = 250;
}
