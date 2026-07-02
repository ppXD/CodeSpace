using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Pure reader for the engine's system scope (<c>{{sys.*}}</c>) — the single place nodes pull
/// run-level context from <see cref="NodeRunContext.Scope"/>. Lives next to <see cref="NodeRunScope"/>
/// / <see cref="SystemScopeKeys"/> so every reader of those keys shares one implementation.
/// </summary>
public static class NodeScopeReader
{
    /// <summary>
    /// The run's owning team from <c>{{sys.team_id}}</c>. The workflow engine always populates this, so a
    /// real run resolves it; a synthetic agent-tool context with no scope does NOT — returns false there so
    /// repo-resolving nodes fail closed (a model-supplied repositoryId can never escape the run's tenant).
    /// Requires the value to be a JSON string parseable as a <see cref="Guid"/>.
    /// </summary>
    public static bool TryReadTeamId(NodeRunContext context, out Guid teamId)
    {
        teamId = Guid.Empty;
        if (!context.Scope.Sys.TryGetValue(SystemScopeKeys.TeamId, out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out teamId);
    }

    /// <summary>
    /// The run's id from <c>{{sys.workflow_run_id}}</c> — same population/absence semantics as
    /// <see cref="TryReadTeamId"/>: a real engine run always resolves it; a synthetic no-scope context returns
    /// false so run-linked persistence (e.g. the plan.author node's work-plan row) fails closed instead of
    /// writing an unowned row.
    /// </summary>
    public static bool TryReadWorkflowRunId(NodeRunContext context, out Guid workflowRunId)
    {
        workflowRunId = Guid.Empty;
        if (!context.Scope.Sys.TryGetValue(SystemScopeKeys.WorkflowRunId, out var value) || value.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(value.GetString(), out workflowRunId);
    }
}
