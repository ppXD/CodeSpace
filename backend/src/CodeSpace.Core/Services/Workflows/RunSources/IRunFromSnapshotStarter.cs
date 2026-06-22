using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Starts a one-shot workflow run from an INLINE FROZEN definition snapshot — the
/// dynamic-workflows substrate. Unlike <see cref="IRunStarter"/> (which stages a run pinned to a
/// persisted <c>workflow_version</c>), this seam stages a run that carries its own
/// <see cref="WorkflowDefinition"/> on the run row and creates NO <c>workflow</c> /
/// <c>workflow_version</c> row. The run flows through the EXACT same durable engine
/// (executor → suspend/resume → dispatch) — only the definition SOURCE differs.
///
/// <para>Kept as a sibling capability (Rule 7) rather than widening <see cref="IRunStarter"/>:
/// the snapshot path validates + hashes a caller-supplied definition and owns its OWN
/// transaction + dispatch, where <see cref="IRunStarter"/> only stages onto the caller's change
/// tracker. Folding both onto one interface would force every <see cref="IRunStarter"/> caller to
/// reason about a definition they never supply.</para>
/// </summary>
public interface IRunFromSnapshotStarter
{
    /// <summary>
    /// Validate <paramref name="definition"/> (reusing the engine's <c>DefinitionValidator</c>),
    /// stage the <c>workflow_run_request</c> + snapshot <c>workflow_run</c> rows in one transaction
    /// (frozen definition JSON + its canonical hash on the run; NO workflow / workflow_version row),
    /// then dispatch through the EXISTING <c>IWorkflowRunDispatcher</c> (Pending → Enqueued → Hangfire
    /// <c>ExecuteRunAsync</c>). Returns the new <c>workflow_run.id</c>. Throws
    /// <c>WorkflowValidationException</c> for an invalid definition (before any DB write).
    /// </summary>
    Task<Guid> StartFromSnapshotAsync(WorkflowDefinition definition, Guid teamId, Guid actorUserId, string? launchPayloadJson, IReadOnlyList<Guid>? scopeRepositoryIds, string? projectionKind, CancellationToken cancellationToken);

    /// <summary>
    /// Replay a finished snapshot/dynamic run: clone its EXACT frozen definition (<paramref name="definitionJson"/>
    /// + <paramref name="definitionHash"/>) onto a NEW snapshot run — no re-validate / re-freeze, so replay
    /// reproduces the definition that ran byte-for-byte. STAGE-ONLY (one tx) + <c>run.queued</c>(Replay), carrying
    /// the replay lineage (<paramref name="parentRunId"/> drives the engine's <c>run.replayed</c>;
    /// <paramref name="causationRequestId"/> links to the original request). Unlike <see cref="StartFromSnapshotAsync"/>
    /// this does NOT dispatch — the caller (<c>WorkflowService.ReplayRunAsync</c>) clones the original's variable
    /// snapshot then dispatches, exactly as the authored-replay path does, so the engine's variable-presence fork
    /// takes the replay scope. Returns the new <c>workflow_run.id</c>.
    /// </summary>
    Task<Guid> StageReplayFromSnapshotAsync(string definitionJson, string definitionHash, Guid teamId, Guid actorUserId, string payloadJson, string sourceType, Guid parentRunId, Guid causationRequestId, IReadOnlyList<Guid> scopeRepositoryIds, IReadOnlyList<Guid> scopeProjectIds, string? projectionKind, CancellationToken cancellationToken);
}
