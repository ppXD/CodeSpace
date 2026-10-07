using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the approval sweep's follow-ups — the in-process wake and the card mirror — stay best-effort per row and per
/// step. A caller with no transaction runs them inline as the sweep reaches each row, and nothing there swallows a
/// failure: one card's failed mirror must still leave that row woken and every later row woken and mirrored, a failed wake
/// must still leave its own card mirrored, and the count of approvals durably expired stands. Each swallowed failure names
/// its ledger row in the log, since the drain that would otherwise catch it cannot say which row it was.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ToolApprovalExpiryServiceTests
{
    [Fact]
    public async Task A_failed_card_mirror_leaves_every_other_expired_approval_woken_and_mirrored()
    {
        var first = Expired();
        var second = Expired();
        var third = Expired();
        var waiters = new RecordingWaiters();
        var cards = new CardsFailingFor(second.ApprovalMessageId!.Value);
        var log = new CapturingLogger();
        var service = new ToolApprovalExpiryService(new ExpiredLedger(first, second, third), waiters, cards, new InlinePostCommitActions(), log);

        (await service.ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(3, "all three approvals were durably expired, whatever their follow-ups did");

        waiters.Signalled.ShouldBe(new[] { (first.LedgerId, ToolApprovalOutcome.Expired), (second.LedgerId, ToolApprovalOutcome.Expired), (third.LedgerId, ToolApprovalOutcome.Expired) }, ignoreOrder: false, customMessage: "each approval's blocked call is woken with Expired, the row whose mirror failed included");
        cards.Mirrored.ShouldBe(new[] { first.ApprovalMessageId!.Value, third.ApprovalMessageId!.Value }, ignoreOrder: false, customMessage: "the second card's mirror failed, and the third's still ran");

        var warning = log.Entries.ShouldHaveSingleItem("the one failed step is logged once");
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeOfType<InvalidOperationException>("the failure itself travels with the entry");
        warning.Properties["LedgerId"].ShouldBe(second.LedgerId, "the entry names the row whose card was not mirrored");
    }

    [Fact]
    public async Task A_failed_wake_still_mirrors_its_card()
    {
        var approval = Expired();
        var cards = new CardsFailingFor(Guid.Empty);
        var log = new CapturingLogger();
        var service = new ToolApprovalExpiryService(new ExpiredLedger(approval), new WaitersFailingFor(approval.LedgerId), cards, new InlinePostCommitActions(), log);

        (await service.ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(1, "the approval was durably expired, whatever its follow-ups did");

        cards.Mirrored.ShouldBe(new[] { approval.ApprovalMessageId!.Value }, ignoreOrder: false, customMessage: "the wake and the mirror are separate steps: a wake that threw costs the card nothing");
        log.Entries.ShouldHaveSingleItem("the one failed step is logged once").Properties["LedgerId"].ShouldBe(approval.LedgerId, "the entry names the row whose call was not woken");
    }

    [Fact]
    public async Task An_approval_that_never_posted_a_card_is_woken_and_has_nothing_to_mirror()
    {
        var approval = Expired(withCard: false);
        var waiters = new RecordingWaiters();
        var cards = new CardsFailingFor(Guid.Empty);
        var service = new ToolApprovalExpiryService(new ExpiredLedger(approval), waiters, cards, new InlinePostCommitActions(), new CapturingLogger());

        (await service.ExpireDueAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(1);

        waiters.Signalled.ShouldBe(new[] { (approval.LedgerId, ToolApprovalOutcome.Expired) }, ignoreOrder: false, customMessage: "the blocked call is woken whether or not a card was ever posted");
        cards.Mirrored.ShouldBeEmpty("no card was recorded on the row, so there is nothing to mirror");
    }

    [Fact]
    public async Task A_shutdown_that_cancels_a_mirror_stops_the_sweep_instead_of_being_logged_away()
    {
        using var shutdown = new CancellationTokenSource();
        var first = Expired();
        var second = Expired();
        var waiters = new RecordingWaiters();
        var log = new CapturingLogger();
        var service = new ToolApprovalExpiryService(new ExpiredLedger(first, second), waiters, new CardsCancelledBy(shutdown), new InlinePostCommitActions(), log);

        await Should.ThrowAsync<OperationCanceledException>(() => service.ExpireDueAsync(DateTimeOffset.UtcNow, shutdown.Token));

        waiters.Signalled.ShouldBe(new[] { (first.LedgerId, ToolApprovalOutcome.Expired) }, ignoreOrder: false, customMessage: "the sweep stopped at the row the shutdown reached; the rows it never got to are not swept on a token that is cancelled");
        log.Entries.ShouldBeEmpty("a stop is not a failed mirror, so nothing is logged as one");
    }

    private static ExpiredToolApproval Expired(bool withCard = true) => new() { LedgerId = Guid.NewGuid(), TeamId = Guid.NewGuid(), ApprovalMessageId = withCard ? Guid.NewGuid() : null };

    /// <summary>No transaction is open, as when the service is called outside its command: an action runs the moment it is handed over.</summary>
    private sealed class InlinePostCommitActions : IPostCommitActions
    {
        public Task RunAfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => action(cancellationToken);
        public Task RunAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public int CreateCheckpoint() => 0;
        public void RollbackTo(int checkpoint) { }
    }

    private sealed class RecordingWaiters : IToolApprovalWaiterRegistry
    {
        public List<(Guid LedgerId, ToolApprovalOutcome Outcome)> Signalled { get; } = new();

        public IToolApprovalWaiter Register(Guid ledgerId) => throw new NotSupportedException();
        public bool TrySignal(Guid ledgerId, ToolApprovalOutcome outcome)
        {
            Signalled.Add((ledgerId, outcome));
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

    /// <summary>A host shutting down under the sweep: the first card mirror it reaches cancels the token the sweep runs on, and is itself cancelled by it.</summary>
    private sealed class CardsCancelledBy : IMessageInteractionService
    {
        private readonly CancellationTokenSource _shutdown;

        public CardsCancelledBy(CancellationTokenSource shutdown) { _shutdown = shutdown; }

        public Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkTimedOutAsync(Guid messageId, string responseKey, CancellationToken cancellationToken)
        {
            _shutdown.Cancel();

            throw new OperationCanceledException(_shutdown.Token);
        }
    }

    /// <summary>Captures each warning's level, exception and structured properties — the named template values an interpolated message would not carry. The per-row Information line is not what these tests are about, so nothing below Warning is kept.</summary>
    private sealed class CapturingLogger : ILogger<ToolApprovalExpiryService>
    {
        public List<(LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;

            Entries.Add((logLevel, exception, (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(p => p.Key, p => p.Value)));
        }
    }

    /// <summary>A ledger whose sweep expired exactly the approvals handed in; every other ledger method is unreachable here.</summary>
    private sealed class ExpiredLedger : IToolCallLedgerService
    {
        private readonly IReadOnlyList<ExpiredToolApproval> _expired;

        public ExpiredLedger(params ExpiredToolApproval[] expired) { _expired = expired; }

        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(_expired);

        public IAsyncEnumerable<TimedOutDecision> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
