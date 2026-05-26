using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Create a workflow from a definition + optional activations. Definition is validated
/// (schema + graph integrity) before any DB writes; failure returns 400 with the validator
/// errors list. The first version is persisted to <c>workflow_version</c> + the live
/// <c>definition_jsonb</c> on the workflow row.
/// </summary>
public sealed record CreateWorkflowCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required WorkflowDefinition Definition { get; init; }

    /// <summary>Configures the run sources for this workflow.</summary>
    public required IReadOnlyList<WorkflowActivationInput> Activations { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Input shape for creating / replacing an activation row on a workflow. TypeKey
/// discriminates source family (e.g. "provider.github.pull_request", "schedule.cron",
/// "manual"); Config holds the source-specific filter / parameters JSON.
/// </summary>
public sealed record WorkflowActivationInput
{
    public required string TypeKey { get; init; }
    public required System.Text.Json.JsonElement Config { get; init; }
    public bool Enabled { get; init; } = true;
}
