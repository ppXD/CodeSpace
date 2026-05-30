using System.Text.Json;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Workflow CRUD + execution kickoff. Rule 16 — every handler under
/// CodeSpace.Core.Handlers/{Command,Query}Handlers/Workflows is a thin shell delegating here.
/// </summary>
public interface IWorkflowService
{
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);
    Task<WorkflowDetail?> GetAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, bool enabled, CancellationToken cancellationToken);
    Task UpdateAsync(Guid workflowId, Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, CancellationToken cancellationToken);
    Task DeleteAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken);
    Task SetEnabledAsync(Guid workflowId, Guid teamId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a <c>workflow_run_request</c> (source_type="manual", actor=user) and then a
    /// <c>workflow_run</c> pointing at it. Returns the run id so the SPA can navigate.
    /// </summary>
    Task<Guid> RunManuallyAsync(Guid workflowId, Guid teamId, Guid actorUserId, JsonElement? payload, CancellationToken cancellationToken);

    /// <summary>
    /// Clones an existing run as a fresh replay. Creates a new <c>workflow_run_request</c>
    /// (source_type="replay", causation_id = original.request_id) and a <c>workflow_run</c>
    /// that reuses the original's workflow_version, release_hash, and variable snapshot rows.
    /// Returns the new run's id. Throws <see cref="KeyNotFoundException"/> if the original
    /// isn't in the caller's team.
    /// </summary>
    Task<Guid> ReplayRunAsync(Guid originalRunId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(Guid workflowId, Guid teamId, int limit, CancellationToken cancellationToken);
    Task<WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a pending <c>Approval</c> wait on a Suspended run with a human decision
    /// (approved + optional comment) and resume it. Tenancy: the run's workflow must belong to
    /// the caller's team (<see cref="KeyNotFoundException"/> conflated with not-yours). Returns
    /// false when the run has no pending approval wait — already resolved, not suspended, or
    /// parked on a different signal (timer / callback).
    /// </summary>
    Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken);

    IReadOnlyList<NodeManifestDto> ListNodeManifests();

    /// <summary>
    /// Canonical list of engine-injected <c>sys.*</c> variables — fixed per release. Feeds
    /// the editor's read-only System tab + the {{}} autocomplete picker so frontend doesn't
    /// have to mirror a parallel list. Sourced from <c>SystemScopeKeys.Descriptors</c>.
    /// </summary>
    IReadOnlyList<SystemVariableDto> ListSystemVariables();
}
