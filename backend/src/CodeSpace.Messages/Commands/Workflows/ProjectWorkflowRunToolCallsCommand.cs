using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>Bounded shadow projection of governed Workflow Run tool calls into first-class observation rows.</summary>
public sealed record ProjectWorkflowRunToolCallsCommand : ICommand<int>
{
    public int BatchSize { get; init; } = 250;
}
