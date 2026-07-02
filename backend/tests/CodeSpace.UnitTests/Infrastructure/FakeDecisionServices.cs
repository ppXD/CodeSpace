using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;

namespace CodeSpace.UnitTests.Infrastructure;

/// <summary>
/// Configurable fakes for the three decision collaborators the supervisor arbiter drain (D4c-2) injects — the honest
/// seam (only the DB / brain calls are replaced). Default-constructed they are NO-OPs (empty queue, escalate verdict,
/// Answered outcome) so an existing supervisor ctor site that never has pending children compiles + behaves identically.
/// </summary>
public sealed class FakeDecisionQueue : IDecisionQueueService
{
    private readonly IReadOnlyList<PendingDecision> _pendingForAgents;

    public FakeDecisionQueue(params PendingDecision[] pendingForAgents) => _pendingForAgents = pendingForAgents;

    public IReadOnlyCollection<Guid>? LastAgentRunIds { get; private set; }

    public Task<IReadOnlyList<PendingDecision>> ListPendingAsync(Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingDecision>>(Array.Empty<PendingDecision>());

    public Task<IReadOnlyList<PendingDecision>> ListPendingForAgentRunsAsync(IReadOnlyCollection<Guid> agentRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        LastAgentRunIds = agentRunIds;

        return Task.FromResult(_pendingForAgents);
    }
}

/// <summary>A scripted <see cref="IDecisionArbiter"/> — returns a configured verdict (default escalate, the fail-closed steady state) and records each decision it judged + the inputs it was given. Honours cancellation (throws), mirroring the real arbiter's one propagating exception.</summary>
public sealed class FakeDecisionArbiter : IDecisionArbiter
{
    private readonly Func<PendingDecision, ArbiterVerdict> _verdict;

    public FakeDecisionArbiter(Func<PendingDecision, ArbiterVerdict>? verdict = null) =>
        _verdict = verdict ?? (_ => ArbiterVerdict.Escalate("(test escalate)"));

    public List<Guid> JudgedDecisionIds { get; } = new();
    public Guid? LastTeamId { get; private set; }
    public Guid? LastSupervisorModelId { get; private set; }
    public string? LastGoal { get; private set; }

    public Task<ArbiterVerdict> DecideAsync(PendingDecision decision, Guid teamId, Guid? supervisorModelId, string goal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        JudgedDecisionIds.Add(decision.Id);
        LastTeamId = teamId;
        LastSupervisorModelId = supervisorModelId;
        LastGoal = goal;

        return Task.FromResult(_verdict(decision));
    }
}

/// <summary>A scripted <see cref="IDecisionAnswerService"/> — records every supervisor-author answer and returns a configured outcome (default Answered). With <c>throwOnCall</c> it throws an infra-style exception on the Nth call (1-based) BEFORE recording, to prove the drain's per-child isolation. The human <c>AnswerAsync</c> is never used by the drain, so it throws if hit (a guard against accidental wiring).</summary>
public sealed class FakeDecisionAnswerService : IDecisionAnswerService
{
    private readonly DecisionAnswerOutcome _outcome;
    private readonly int _throwOnCall;
    private int _calls;

    public FakeDecisionAnswerService(DecisionAnswerOutcome outcome = DecisionAnswerOutcome.Answered, int throwOnCall = 0)
    {
        _outcome = outcome;
        _throwOnCall = throwOnCall;
    }

    public List<SupervisorAnswerCall> Calls { get; } = new();

    public Task<AnswerDecisionResult> AnswerAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the arbiter drain answers only as the supervisor — the human path is never hit here");

    public Task<AnswerDecisionResult> AnswerAsSupervisorAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, string rationale, Guid teamId, CancellationToken cancellationToken)
    {
        _calls++;

        if (_calls == _throwOnCall) throw new InvalidOperationException("simulated infra failure answering this child");

        Calls.Add(new SupervisorAnswerCall(decisionId, selectedOptions, freeText, rationale, teamId));

        return Task.FromResult(AnswerDecisionResult.Of(_outcome));
    }
}

/// <summary>One recorded supervisor-author answer — the exact args the drain passed, for asserting the verdict mapped through verbatim.</summary>
public sealed record SupervisorAnswerCall(Guid DecisionId, IReadOnlyList<string> SelectedOptions, string? FreeText, string Rationale, Guid TeamId);

/// <summary>
/// In-memory <c>IWorkPlanService</c> for the S3 plan-confirmation gate's unit seams. Default-constructed it holds NO
/// plan — <c>GetCurrentAsync</c> returns null, so the gate degrades open and every pre-S3 ctor site behaves
/// identically. Seed a row to drive the gate; <c>StatusFlips</c> records each CAS so tests assert the transitions.
/// </summary>
public sealed class FakeWorkPlanStore : CodeSpace.Core.Services.Plans.IWorkPlanService
{
    private readonly CodeSpace.Core.Persistence.Entities.WorkPlan? _current;

    public FakeWorkPlanStore(CodeSpace.Core.Persistence.Entities.WorkPlan? current = null) => _current = current;

    public List<(string From, string To)> StatusFlips { get; } = new();

    public Task<CodeSpace.Core.Persistence.Entities.WorkPlan> SaveVersionAsync(CodeSpace.Messages.Plans.WorkPlanDraft draft, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The turn-loop gate never saves plans.");

    public Task<CodeSpace.Core.Persistence.Entities.WorkPlan?> GetCurrentAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken, string? originKind = null) =>
        Task.FromResult(_current != null && (originKind == null || _current.OriginKind == originKind) ? _current : null);

    public Task<IReadOnlyList<CodeSpace.Core.Persistence.Entities.WorkPlan>> ListVersionsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CodeSpace.Core.Persistence.Entities.WorkPlan>>(_current == null ? Array.Empty<CodeSpace.Core.Persistence.Entities.WorkPlan>() : new[] { _current });

    public Task<bool> SetStatusAsync(Guid planId, Guid teamId, string fromStatus, string toStatus, CancellationToken cancellationToken)
    {
        StatusFlips.Add((fromStatus, toStatus));

        if (_current == null || _current.Id != planId || _current.Status != fromStatus) return Task.FromResult(false);

        _current.Status = toStatus;

        return Task.FromResult(true);
    }
}
