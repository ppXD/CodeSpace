using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Resumes the <c>agent.code</c> node parked on a now-terminal agent run. When the executor finishes a
/// run a workflow node spawned (<see cref="AgentRun.WorkflowRunId"/> set), this maps the run's
/// <c>AgentRunResult</c> onto the node's resume payload — <c>{ status, summary, changedFiles, branch,
/// error }</c> — and resumes the workflow run, which re-runs the node so it turns that into outputs
/// (Succeeded) or a clean node failure (anything else, composing with retry + the error branch).
///
/// Best-effort + idempotent per the <see cref="IAgentRunCompletionNotifier"/> contract: a no-op for a run
/// with no workflow link or no still-pending wait (a replay / double-notify resolved the wait already),
/// and a resume failure is logged rather than thrown back into the executor — the agent run already
/// committed its terminal result, and the stuck-run reconciler is the backstop for a lost resume.
/// </summary>
public sealed class WorkflowResumeAgentRunCompletionNotifier : IAgentRunCompletionNotifier, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowResumeService _resumeService;
    private readonly ILogger<WorkflowResumeAgentRunCompletionNotifier> _logger;

    public WorkflowResumeAgentRunCompletionNotifier(CodeSpaceDbContext db, IWorkflowResumeService resumeService, ILogger<WorkflowResumeAgentRunCompletionNotifier> logger)
    {
        _db = db;
        _resumeService = resumeService;
        _logger = logger;
    }

    public async Task NotifyCompletedAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == agentRunId, cancellationToken).ConfigureAwait(false);

        if (run is null || run.WorkflowRunId is null) return;

        try
        {
            await ResumeParkedNodeAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {AgentRunId} completed but resuming workflow run {WorkflowRunId} failed", agentRunId, run.WorkflowRunId);
        }
    }

    /// <summary>
    /// Resume the parent run on THIS agent run's completion. Resolves ONLY this run's own AgentRun wait
    /// (its Token is the agent-run id) — never the sibling waits a parallel agent.code wave holds — so each
    /// node resumes with its own result. A no-op when no pending wait matches (replay / double-notify).
    /// </summary>
    private async Task ResumeParkedNodeAsync(AgentRun run, CancellationToken cancellationToken)
    {
        // Resume ONLY on a terminal run. A non-terminal status here means the notifier fired while the run
        // was still in flight — a completion racing the reconciler, or an inconsistent mid-transition row —
        // and resuming would hand the agent.code node a non-terminal "Running"/"Queued" status, which it can
        // only read as failure ("Agent run did not succeed: Running"). Skip: the reconciler terminalizes a
        // genuinely stuck run and re-fires this, so the node always resumes with a real terminal result.
        if (!AgentRunStateMachine.IsTerminal(run.Status))
        {
            _logger.LogWarning("Agent run {AgentRunId} resume skipped — status {Status} is not terminal; deferring to the reconciler", run.Id, run.Status);
            return;
        }

        var token = run.Id.ToString();

        var waitId = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == run.WorkflowRunId && w.WaitKind == WorkflowWaitKinds.AgentRun
                        && w.Token == token && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (waitId is null) return;

        await _resumeService.ResumeOnWaitCompletionAsync(run.WorkflowRunId!.Value, waitId.Value, BuildResumePayload(run), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Map the durable run + its <c>AgentRunResult</c> onto the flat payload the agent.code node reads on resume.</summary>
    private static string BuildResumePayload(AgentRun run)
    {
        var result = string.IsNullOrWhiteSpace(run.ResultJson) ? null : JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options);

        return JsonSerializer.Serialize(new
        {
            status = run.Status.ToString(),
            summary = result?.Summary,
            changedFiles = result?.ChangedFiles,
            branch = result?.ProducedBranch,
            // Multi-repo: the per-repo change set the agent.code node surfaces for a downstream git.open_change_set.
            // Null/empty for a single-repo run, so the node's single-repo outputs are unchanged.
            repositoryResults = result?.RepositoryResults,
            changeSetId = result?.ChangeSetId,
            error = result?.Error ?? run.Error,
        }, AgentJson.Options);
    }
}
