using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// The tape-grounded <see cref="IWorkPlanConfirmationService"/>: find the run's newest ask_human decision,
/// require it to be an UNANSWERED confirmation card (the <c>SupervisorPlanConfirmation</c> marker), then
/// resolve its Action wait through <see cref="IWorkflowResumeService.ResumeByActionTokenAsync"/> — the same
/// authenticated resume the conversation card's Answer button rides, so both surfaces converge on ONE wait
/// and the supervisor folds ONE answer.
/// </summary>
public sealed class WorkPlanConfirmationService : IWorkPlanConfirmationService, IScopedDependency
{
    /// <summary>The stable prefix a NON-approving answer gets when the operator's feedback itself happens to start with the approve word — without it, "approve nothing until…" typed under Request-changes would read as an approval (the gate matches the folded answer's leading word). Pinned by a unit test.</summary>
    public const string RevisionPrefix = "revise: ";

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowResumeService _resume;

    public WorkPlanConfirmationService(CodeSpaceDbContext db, IWorkflowResumeService resume)
    {
        _db = db;
        _resume = resume;
    }

    public async Task<WorkPlanConfirmationOutcome?> AnswerAsync(Guid workflowRunId, Guid teamId, Guid actorUserId, bool approve, string? feedback, CancellationToken cancellationToken)
    {
        var token = await FindPendingConfirmationTokenAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false)
            ?? await FindPendingGraphConfirmationTokenAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (token == null) return null;

        var comment = ComposeAnswer(approve, feedback);

        var resumed = await _resume.ResumeByActionTokenAsync(token, RealSupervisorActionExecutor.AnswerActionKey, actorUserId, comment, values: null, teamId, cancellationToken).ConfigureAwait(false);

        return new WorkPlanConfirmationOutcome { Resumed = resumed == ActionResumeResult.Resumed, Approved = approve };
    }

    /// <summary>
    /// The pending confirmation card's wait token, or null when there is none. The NEWEST ask_human row must
    /// itself be the unanswered confirmation card — a later content question, an already-answered card, or a
    /// degraded no-surface park (no token) all read as "nothing to confirm" rather than resuming the wrong wait.
    /// </summary>
    private async Task<string?> FindPendingConfirmationTokenAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var last = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == workflowRunId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .OrderByDescending(d => d.Sequence)
            .Select(d => new { d.PayloadJson, d.OutcomeJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (last == null || !SupervisorPlanConfirmation.QuestionCarriesMarker(last.PayloadJson)) return null;

        if (SupervisorOutcome.ReadAskHumanAnswer(last.OutcomeJson) != null) return null;

        return SupervisorOutcome.ReadHumanWaitToken(last.OutcomeJson);
    }

    /// <summary>
    /// The GRAPH-TIER pending confirmation (S4d): a <c>plan.confirm</c> node's park — a Pending Action wait whose
    /// suspend payload carries the <c>plan-confirm</c> discriminator (stamped by the node, so no definition parsing).
    /// Team-scoped through the run row. Null when nothing is parked — the supervisor lookup above already ran, so
    /// both tiers conflate to one 404.
    /// </summary>
    private async Task<string?> FindPendingGraphConfirmationTokenAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var candidates = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == workflowRunId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.PayloadJson != null)
            .Join(_db.WorkflowRun.AsNoTracking().Where(r => r.TeamId == teamId), w => w.RunId, r => r.Id, (w, r) => new { w.Token, w.PayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
            if (IsPlanConfirmWait(candidate.PayloadJson!)) return candidate.Token;

        return null;
    }

    private static bool IsPlanConfirmWait(string payloadJson)
    {
        try
        {
            var root = JsonDocument.Parse(payloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String && kind.GetString() == Workflows.Nodes.Builtin.PlanConfirmNode.WaitPayloadKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compose the folded answer: an approval LEADS with the approve word (the gate's release predicate),
    /// optionally carrying a trailing note; revision feedback is passed through verbatim — unless it would
    /// itself read as an approval, in which case it gets the <see cref="RevisionPrefix"/> (fail-closed:
    /// a Request-changes click can never accidentally release the gate). Internal for direct unit pinning.
    /// </summary>
    internal static string ComposeAnswer(bool approve, string? feedback)
    {
        var note = feedback?.Trim();

        if (approve) return string.IsNullOrEmpty(note) ? SupervisorApprovalRequest.ApproveReply : $"{SupervisorApprovalRequest.ApproveReply} — {note}";

        if (string.IsNullOrEmpty(note))
            throw new ArgumentException("Revision feedback is required when not approving the plan.", nameof(feedback));

        return note.StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase) ? $"{RevisionPrefix}{note}" : note;
    }
}
