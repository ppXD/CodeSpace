using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the supervisor-decision reaper dispatch chain stays thin (Rule 14 + Rule 16). The recurring job sends
/// exactly the <see cref="ExpireStaleSupervisorDecisionsCommand"/> on a minutely cadence (no logic of its own — the lane
/// is always on, so the reaper is unconditionally registered), and the command handler derives the cutoff + forwards to
/// <see cref="ISupervisorDecisionLog.ExpireStalePendingAsync"/> and returns its count (no logic of its own). Hand-rolled
/// recording doubles (no mocking lib, matching the codebase convention).
/// </summary>
[Trait("Category", "Unit")]
public class ExpireStaleSupervisorDecisionsDispatchTests
{
    [Fact]
    public async Task The_recurring_job_dispatches_the_expire_command_minutely()
    {
        var mediator = new RecordingMediator();
        var job = new ExpireStaleSupervisorDecisionsRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(ExpireStaleSupervisorDecisionsRecurringJob));
        job.CronExpression.ShouldBe(Cron.Minutely(), "the reaper runs every minute — responsive + cheap");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ExpireStaleSupervisorDecisionsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_handler_forwards_to_the_log_and_returns_its_count()
    {
        var log = new RecordingDecisionLog { ToReturn = 7 };
        var handler = new ExpireStaleSupervisorDecisionsCommandHandler(log);

        var result = await handler.Handle(new ExpireStaleSupervisorDecisionsCommand(), CancellationToken.None);

        log.Calls.ShouldBe(1, "the handler delegates the whole sweep to the log (Rule 16)");
        result.Expired.ShouldBe(7, "the handler surfaces the log's expired count verbatim");
        log.LastCutoff.ShouldBeLessThan(DateTimeOffset.UtcNow, "the cutoff is in the past — only stale rows are swept");
    }

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

    /// <summary>Records the sweep call + returns a canned count, so the handler test asserts pure delegation. The rest of the surface is unreachable here.</summary>
    private sealed class RecordingDecisionLog : ISupervisorDecisionLog
    {
        public int Calls;
        public int ToReturn;
        public DateTimeOffset LastCutoff;

        public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        {
            Calls++;
            LastCutoff = olderThan;
            return Task.FromResult(ToReturn);
        }

        public Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
