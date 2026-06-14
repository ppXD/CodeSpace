using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Records the human's DECISION (approve / reject) on a parked tool-call approval (durable mid-turn HITL, item D) and
/// wakes any in-memory waiter so a blocked handler call (item D2) resumes. It does NOT run the side effect — approve
/// only STAMPS the decision (the row stays <c>AwaitingApproval</c>; the handler flips it to terminal once it executes);
/// reject drives <c>AwaitingApproval → Failed</c> directly. Owns the status-guarded CAS over the ledger row (mirrors
/// <see cref="ToolCallLedgerService"/>.RecordTerminalAsync) and team-scopes every read for defense-in-depth (mirrors
/// <c>WorkflowResumeService.ResumeByActionTokenAsync</c>). Returns an <see cref="ActionResumeResult"/> so the chat
/// caller knows whether to stamp the card (Resumed / NoWait) or reject a late click (AlreadyResolved).
/// </summary>
public interface IToolCallApprovalResolver
{
    /// <summary>Record an approve/reject verdict on the approval whose token is <paramref name="token"/>, team-scoped to <paramref name="teamId"/>. Resumed when this click decided it; NoWait when no parked approval exists for the team (or the responseKey isn't approve/reject); AlreadyResolved when a deadline / another responder / the handler already moved the row.</summary>
    Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, Guid actorUserId, Guid teamId, CancellationToken ct);
}

public sealed class ToolCallApprovalResolver : IToolCallApprovalResolver, IScopedDependency
{
    private const string Approve = "approve";
    private const string Reject = "reject";

    private readonly CodeSpaceDbContext _db;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly ILogger<ToolCallApprovalResolver> _logger;

    public ToolCallApprovalResolver(CodeSpaceDbContext db, IToolApprovalWaiterRegistry waiters, ILogger<ToolCallApprovalResolver> logger)
    {
        _db = db;
        _waiters = waiters;
        _logger = logger;
    }

    public async Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, Guid actorUserId, Guid teamId, CancellationToken ct)
    {
        // Team-scoped fresh + untracked read (defense-in-depth — a leaked/cross-team token finds nothing). The partial
        // index on approval_token (migration 0049) keeps this lookup tiny.
        var row = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.ApprovalToken == token && l.TeamId == teamId)
            .Select(l => new { l.Id, l.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (row == null)
        {
            _logger.LogDebug("Tool-call approval resolve: no parked approval matches the token in team {TeamId} — caller records the click", teamId);
            return ActionResumeResult.NoWait;
        }

        if (row.Status != ToolCallLedgerStatus.AwaitingApproval)
        {
            _logger.LogDebug("Tool-call approval resolve: ledger {LedgerId} is {Status}, not AwaitingApproval — rejecting the late click", row.Id, row.Status);
            return ActionResumeResult.AlreadyResolved;
        }

        // The approval card only ever emits "approve" / "reject"; any other key is a fail-safe that records the response
        // in the living thread WITHOUT resolving the approval — it must never approve or fail the row.
        return responseKey switch
        {
            Reject => await RejectAsync(row.Id, teamId, actorUserId, ct).ConfigureAwait(false),
            Approve => await ApproveAsync(row.Id, teamId, actorUserId, ct).ConfigureAwait(false),
            _ => NoWaitForUnknownKey(row.Id, responseKey),
        };
    }

    // Reject — status-guarded CAS AwaitingApproval → Failed (a legal transition). No side effect to run.
    private async Task<ActionResumeResult> RejectAsync(Guid ledgerId, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var affected = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ToolCallLedgerStatus.Failed)
                .SetProperty(l => l.Error, $"rejected by {actorUserId}")
                .SetProperty(l => l.LastModifiedDate, now)
                .SetProperty(l => l.LastModifiedBy, actorUserId), ct)
            .ConfigureAwait(false);

        if (affected == 0) return ActionResumeResult.AlreadyResolved;

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Rejected);

        _logger.LogInformation("Tool-call approval rejected. LedgerId={LedgerId} By={ActorUserId}", ledgerId, actorUserId);
        return ActionResumeResult.Resumed;
    }

    // Approve — status-guarded CAS that STAMPS the decision WITHOUT changing status (the row stays AwaitingApproval; the
    // handler flips it to terminal once it runs the side effect). The approved_at == null guard makes a concurrent
    // approve idempotent: exactly one stamp wins, the loser sees affected == 0 → AlreadyResolved.
    private async Task<ActionResumeResult> ApproveAsync(Guid ledgerId, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var affected = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ApprovedByUserId, actorUserId)
                .SetProperty(l => l.ApprovedAt, now)
                .SetProperty(l => l.LastModifiedDate, now)
                .SetProperty(l => l.LastModifiedBy, actorUserId), ct)
            .ConfigureAwait(false);

        if (affected == 0) return ActionResumeResult.AlreadyResolved;

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Approved);

        _logger.LogInformation("Tool-call approval approved. LedgerId={LedgerId} By={ActorUserId}", ledgerId, actorUserId);
        return ActionResumeResult.Resumed;
    }

    private ActionResumeResult NoWaitForUnknownKey(Guid ledgerId, string responseKey)
    {
        _logger.LogDebug("Tool-call approval resolve: unknown responseKey {ResponseKey} for ledger {LedgerId} — recording without resolving (fail-safe, never approves)", responseKey, ledgerId);
        return ActionResumeResult.NoWait;
    }
}
