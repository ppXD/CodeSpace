using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Records a human's TYPED answer to a parked agent-grain <c>decision.request</c> (Decision substrate D2) and wakes
/// the blocked handler call so it resumes mid-run with the <see cref="DecisionAnswer"/>. The decision-grain analogue
/// of <see cref="IToolCallApprovalResolver"/>: same durable spine (the call parks as a tool-ledger row, the token is
/// the authority, the in-memory waiter is a fast-path), but the clicked button's key is an OPTION id (or the free-text
/// sentinel) rather than approve/reject, so it builds a <see cref="DecisionAnswer"/> instead of a binary verdict. Owns
/// the status-guarded CAS (<see cref="IToolCallLedgerService.TryAnswerDecisionAsync"/>) and team-scopes every read.
/// </summary>
public interface IDecisionRequestResolver
{
    /// <summary>Record the answer (<paramref name="responseKey"/> = the chosen option id, or <see cref="DecisionRequestResolver.FreeTextResponseKey"/> for a free-text submit; <paramref name="comment"/> = the free text / note) on the decision whose token is <paramref name="token"/>, team-scoped. Resumed when this click decided it; NoWait when no parked decision matches; AlreadyResolved when a deadline / another responder / the handler already moved the row.</summary>
    Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, string? comment, Guid actorUserId, Guid teamId, CancellationToken ct);
}

public sealed class DecisionRequestResolver : IDecisionRequestResolver, IScopedDependency
{
    /// <summary>The reserved button key a free-text decision card submits under — distinguishes "free text was entered" from "an option id was chosen" so the resolver leaves <see cref="DecisionAnswer.SelectedOptions"/> empty.</summary>
    public const string FreeTextResponseKey = "__decision_free_text__";

    private readonly CodeSpaceDbContext _db;
    private readonly IToolCallLedgerService _ledger;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly ILogger<DecisionRequestResolver> _logger;

    public DecisionRequestResolver(CodeSpaceDbContext db, IToolCallLedgerService ledger, IToolApprovalWaiterRegistry waiters, ILogger<DecisionRequestResolver> logger)
    {
        _db = db;
        _ledger = ledger;
        _waiters = waiters;
        _logger = logger;
    }

    public async Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, string? comment, Guid actorUserId, Guid teamId, CancellationToken ct)
    {
        // Team-scoped fresh + untracked read, guarded ALSO on ToolKind == decision.request so an approval token can never
        // be resolved through the decision path (and vice versa). A leaked / cross-team token finds nothing → fail-closed.
        var row = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.ApprovalToken == token && l.TeamId == teamId && l.ToolKind == DecisionToolKinds.DecisionRequest)
            .Select(l => new { l.Id, l.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (row == null)
        {
            _logger.LogDebug("Decision resolve: no parked decision matches the token in team {TeamId} — caller records the click", teamId);
            return ActionResumeResult.NoWait;
        }

        if (row.Status != ToolCallLedgerStatus.AwaitingApproval)
        {
            _logger.LogDebug("Decision resolve: ledger {LedgerId} is {Status}, not AwaitingApproval — rejecting the late click", row.Id, row.Status);
            return ActionResumeResult.AlreadyResolved;
        }

        return await AnswerAsync(row.Id, responseKey, comment, actorUserId, teamId, ct).ConfigureAwait(false);
    }

    // Status-guarded CAS AwaitingApproval → Succeeded, stamping the serialized DecisionAnswer. A concurrent dup-click /
    // cross-pod race leaves exactly one winner (true → signal + Resumed); every loser sees affected 0 → AlreadyResolved.
    private async Task<ActionResumeResult> AnswerAsync(Guid ledgerId, string responseKey, string? comment, Guid actorUserId, Guid teamId, CancellationToken ct)
    {
        var answer = BuildAnswer(ledgerId, responseKey, comment, actorUserId);

        var json = JsonSerializer.Serialize(answer, AgentJson.Options);

        var won = await _ledger.TryAnswerDecisionAsync(ledgerId, teamId, json, ct).ConfigureAwait(false);

        if (!won) return ActionResumeResult.AlreadyResolved;

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Approved);

        _logger.LogInformation("Decision answered. LedgerId={LedgerId} By={ActorUserId}", ledgerId, actorUserId);
        return ActionResumeResult.Resumed;
    }

    private static DecisionAnswer BuildAnswer(Guid ledgerId, string responseKey, string? comment, Guid actorUserId) => new()
    {
        DecisionId = ledgerId,
        AnsweredBy = DecisionAnsweredByKinds.Human,
        SelectedOptions = responseKey == FreeTextResponseKey || responseKey.Length == 0 ? Array.Empty<string>() : new[] { responseKey },
        FreeText = string.IsNullOrWhiteSpace(comment) ? null : comment,
        AnsweredByUserId = actorUserId,
    };
}
