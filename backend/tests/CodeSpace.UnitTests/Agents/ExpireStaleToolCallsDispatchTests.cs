using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Decisions;
using Hangfire;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the D6 stranded-tool-call reaper dispatch chain stays thin (Rule 14 + Rule 16). The recurring job sends
/// exactly the <see cref="ExpireStaleToolCallsCommand"/> minutely (no logic of its own), and the handler forwards to
/// <see cref="IToolCallLedgerService.ExpireStaleToolCallsAsync"/> and returns its count verbatim. Hand-rolled recording
/// doubles (no mocking lib, matching the codebase convention).
/// </summary>
[Trait("Category", "Unit")]
public class ExpireStaleToolCallsDispatchTests
{
    [Fact]
    public async Task The_recurring_job_dispatches_the_expire_command_minutely()
    {
        var mediator = new RecordingMediator();
        var job = new ExpireStaleToolCallsRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(ExpireStaleToolCallsRecurringJob));
        job.CronExpression.ShouldBe(Cron.Minutely(), "the reaper runs every minute — responsive + cheap");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ExpireStaleToolCallsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_handler_forwards_to_the_ledger_service_and_returns_its_count()
    {
        var ledger = new RecordingLedger { ToReturn = 3 };
        var handler = new ExpireStaleToolCallsCommandHandler(ledger);

        var result = await handler.Handle(new ExpireStaleToolCallsCommand(), CancellationToken.None);

        ledger.ExpireCalls.ShouldBe(1, "the handler delegates the whole sweep to the ledger service (Rule 16)");
        result.Failed.ShouldBe(3, "the handler surfaces the service's terminalized count verbatim");
    }

    /// <summary>Records the requests sent through the mediator; the rest of the surface is unreachable in these tests.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = new();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<object?>(null);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(request!);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    /// <summary>Records the one method the handler calls + returns a canned count; every other ledger method is unreachable here.</summary>
    private sealed class RecordingLedger : IToolCallLedgerService
    {
        public int ExpireCalls;
        public int ToReturn;

        public Task<int> ExpireStaleToolCallsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            ExpireCalls++;
            return Task.FromResult(ToReturn);
        }

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimedOutDecision>> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
