using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// 🟢 Unit: the decision sweep's follow-ups — the in-process wake and the card mirror — stay best-effort per decision now
/// that the sweep runs outside a transaction. There is no post-commit drain to swallow a failed follow-up any more: they
/// run as the sweep reaches them, so one decision's failed card mirror must still leave the next decision woken and
/// mirrored, a failed wake must still leave its own card mirrored, and the count of decisions durably defaulted stands.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DecisionExpiryServiceTests
{
    [Fact]
    public async Task A_failed_card_mirror_leaves_the_next_decision_woken_and_mirrored()
    {
        var first = new TimedOutDecision { LedgerId = Guid.NewGuid(), TeamId = Guid.NewGuid(), ApprovalMessageId = Guid.NewGuid() };
        var second = new TimedOutDecision { LedgerId = Guid.NewGuid(), TeamId = Guid.NewGuid(), ApprovalMessageId = Guid.NewGuid() };
        var waiters = new RecordingWaiters();
        var cards = new CardsFailingFor(first.ApprovalMessageId!.Value);
        var service = new DecisionExpiryService(new TimedOutLedger(first, second), waiters, cards, new InlinePostCommitActions(), NullLogger<DecisionExpiryService>.Instance);

        (await service.ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(2, "both decisions were durably defaulted, whatever their follow-ups did");

        waiters.Signalled.ShouldBe(new[] { first.LedgerId, second.LedgerId }, ignoreOrder: false, customMessage: "each decision's blocked call is woken");
        cards.Mirrored.ShouldBe(new[] { second.ApprovalMessageId!.Value }, ignoreOrder: false, customMessage: "the first card's mirror failed, and the second's still ran");
    }

    [Fact]
    public async Task A_failed_wake_still_mirrors_its_card()
    {
        var decision = new TimedOutDecision { LedgerId = Guid.NewGuid(), TeamId = Guid.NewGuid(), ApprovalMessageId = Guid.NewGuid() };
        var cards = new CardsFailingFor(Guid.Empty);
        var service = new DecisionExpiryService(new TimedOutLedger(decision), new WaitersFailingFor(decision.LedgerId), cards, new InlinePostCommitActions(), NullLogger<DecisionExpiryService>.Instance);

        (await service.ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(1, "the decision was durably defaulted, whatever its follow-ups did");

        cards.Mirrored.ShouldBe(new[] { decision.ApprovalMessageId!.Value }, ignoreOrder: false, customMessage: "the wake and the mirror are separate steps: a wake that threw costs the card nothing");
    }

    /// <summary>No transaction is open, as under the sweep's non-transactional command: an action runs the moment it is handed over.</summary>
    private sealed class InlinePostCommitActions : IPostCommitActions
    {
        public Task RunAfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => action(cancellationToken);
        public Task RunAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public int CreateCheckpoint() => 0;
        public void RollbackTo(int checkpoint) { }
    }

    private sealed class RecordingWaiters : IToolApprovalWaiterRegistry
    {
        public List<Guid> Signalled { get; } = new();

        public IToolApprovalWaiter Register(Guid ledgerId) => throw new NotSupportedException();
        public bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome)
        {
            Signalled.Add(ledgerId);
            return true;
        }
        public void Remove(Guid ledgerId) => throw new NotSupportedException();
    }

    private sealed class WaitersFailingFor : IToolApprovalWaiterRegistry
    {
        private readonly Guid _failing;

        public WaitersFailingFor(Guid failing) { _failing = failing; }

        public IToolApprovalWaiter Register(Guid ledgerId) => throw new NotSupportedException();
        public bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome) => ledgerId == _failing ? throw new InvalidOperationException("the waiter could not be signalled") : true;
        public void Remove(Guid ledgerId) => throw new NotSupportedException();
    }

    private sealed class CardsFailingFor : IMessageInteractionService
    {
        private readonly Guid _failing;

        public CardsFailingFor(Guid failing) { _failing = failing; }

        public List<Guid> Mirrored { get; } = new();

        public Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkTimedOutAsync(Guid messageId, string responseKey, CancellationToken cancellationToken)
        {
            if (messageId == _failing) throw new InvalidOperationException("the card could not be mirrored");

            Mirrored.Add(messageId);
            return Task.CompletedTask;
        }
    }

    /// <summary>A ledger whose sweep defaulted exactly the decisions handed in; every other ledger method is unreachable here.</summary>
    private sealed class TimedOutLedger : IToolCallLedgerService
    {
        private readonly IReadOnlyList<TimedOutDecision> _timedOut;

        public TimedOutLedger(params TimedOutDecision[] timedOut) { _timedOut = timedOut; }

        public IAsyncEnumerable<TimedOutDecision> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => _timedOut.ToAsyncEnumerable();

        public Task<int> ExpireStaleToolCallsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, ToolCallApprovalPark park, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> WasTargetRejectedAsync(Guid agentRunId, Guid teamId, string approvalTarget, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsTargetAwaitingApprovalAsync(Guid agentRunId, Guid teamId, string approvalTarget, Guid excludeLedgerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallTerminalReplayState?> ReadTerminalForReplayAsync(Guid ledgerId, Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
