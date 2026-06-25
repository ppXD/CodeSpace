using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Resolves an <see cref="AgentTask"/> against its optional Agent persona at dispatch — the "which persona
/// does this run embody" seam. When the task carries an <c>AgentDefinitionId</c>, it merges the persona's
/// system prompt + model into the task (the node only ever carries the reference + inline overrides; it
/// never touches the DB). A task with no persona returns unchanged — the pure-inline run.
///
/// <para>Invoked in <c>WorkflowEngine.StageAgentRunAsync</c> BEFORE the run is persisted, so the merged
/// task is frozen into <c>TaskJson</c>: the run row is self-describing, the executor needs no change, and
/// re-claims/reconcilers replay the same task deterministically even if the persona is later edited. This
/// is why persona resolution lives here, not in the executor (where workspace tokens must be re-resolved
/// fresh per attempt).</para>
/// </summary>
public interface IAgentDefinitionResolver
{
    /// <summary>Merge the task's persona (if any) into it. Throws <see cref="AgentDefinitionResolutionException"/> when a referenced persona can't be resolved for the team or the merge yields nothing to run.</summary>
    Task<AgentTask> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Resolve a persona @-mention SLUG to its team-scoped <c>AgentDefinitionId</c> (the handle is normalized the SAME way authoring derives it, so a name or a slug both resolve). Returns null when no active persona in the team has that slug — the caller decides whether that is fail-closed or a fallback. Team-scoped + <c>DeletedDate == null</c>, so a foreign / soft-deleted slug never resolves.</summary>
    Task<Guid?> ResolveSlugAsync(string slug, Guid teamId, CancellationToken cancellationToken);
}
