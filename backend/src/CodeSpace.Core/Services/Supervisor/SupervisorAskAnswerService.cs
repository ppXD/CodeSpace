using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>Answer a run's pending supervisor ASK from any surface (the run page's inline answer bar, an API caller) — the third answer surface converging on the SAME durable Action wait the conversation card's Answer button resumes.</summary>
public interface ISupervisorAskAnswerService
{
    /// <summary>Answer the run's NEWEST pending supervisor ask. Null when nothing is parked (no unanswered ask / foreign run / degraded no-surface park) — the caller 404-conflates.</summary>
    Task<SupervisorAskAnswerOutcome?> AnswerAsync(Guid workflowRunId, Guid teamId, Guid actorUserId, string answer, CancellationToken cancellationToken);
}

/// <summary>
/// The tape-grounded <see cref="ISupervisorAskAnswerService"/> — the GENERIC sibling of the plan-confirmation
/// service: find the run's newest ask_human decision, require it UNANSWERED, and resolve its Action wait through
/// <see cref="IWorkflowResumeService.ResumeByActionTokenAsync"/> (first answer wins across every surface). Works for
/// EVERY supervisor ask alike — a content question, a review-gate escalation ('approve' = the one-shot absolution,
/// anything else = guidance the next decide reads) — because the ask contract is one mechanism. A plan-CONFIRMATION
/// card keeps its dedicated endpoint (approve/feedback semantics); this service deliberately does not special-case it.
/// </summary>
public sealed class SupervisorAskAnswerService : ISupervisorAskAnswerService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowResumeService _resume;

    public SupervisorAskAnswerService(CodeSpaceDbContext db, IWorkflowResumeService resume)
    {
        _db = db;
        _resume = resume;
    }

    public async Task<SupervisorAskAnswerOutcome?> AnswerAsync(Guid workflowRunId, Guid teamId, Guid actorUserId, string answer, CancellationToken cancellationToken)
    {
        var last = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == workflowRunId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .OrderByDescending(d => d.Sequence)
            .Select(d => new { d.OutcomeJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (last == null) return null;

        if (SupervisorOutcome.ReadAskHumanAnswer(last.OutcomeJson) != null) return null;   // already answered — first answer won

        var token = SupervisorOutcome.ReadHumanWaitToken(last.OutcomeJson);

        if (token == null) return null;   // a degraded no-surface park carries no token — nothing to resume

        var resumed = await _resume.ResumeByActionTokenAsync(token, Executors.RealSupervisorActionExecutor.AnswerActionKey, actorUserId, answer, values: null, teamId, cancellationToken).ConfigureAwait(false);

        return new SupervisorAskAnswerOutcome { Resumed = resumed == ActionResumeResult.Resumed };
    }
}
