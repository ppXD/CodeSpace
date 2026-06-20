using CodeSpace.Core.Handlers.CommandHandlers.Decisions;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Commands.Decisions;
using Hangfire;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// 🟢 Unit: the D5b decision-reaper dispatch chain stays thin (Rule 14 + Rule 16). The recurring job sends exactly the
/// <see cref="ExpireStaleDecisionsCommand"/> on a minutely cadence (no logic of its own), and the command handler forwards
/// to <see cref="IDecisionExpiryService"/> and returns its count (no logic of its own). Hand-rolled recording doubles
/// (no mocking lib, matching the codebase convention + the sibling ExpireStaleToolApprovalsDispatchTests).
/// </summary>
[Trait("Category", "Unit")]
public class DecisionReaperDispatchTests
{
    [Fact]
    public async Task The_recurring_job_dispatches_the_expire_command_minutely()
    {
        var mediator = new RecordingMediator();
        var job = new ExpireStaleDecisionsRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(ExpireStaleDecisionsRecurringJob));
        job.CronExpression.ShouldBe(Cron.Minutely(), "the reaper runs every minute — responsive + cheap");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ExpireStaleDecisionsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_handler_forwards_to_the_expiry_service_and_returns_its_count()
    {
        var service = new RecordingExpiryService { ToReturn = 3 };
        var handler = new ExpireStaleDecisionsCommandHandler(service);

        var result = await handler.Handle(new ExpireStaleDecisionsCommand(), CancellationToken.None);

        service.Calls.ShouldBe(1, "the handler delegates the whole job to the service (Rule 16)");
        result.Defaulted.ShouldBe(3, "the handler surfaces the service's defaulted count verbatim");
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

    private sealed class RecordingExpiryService : IDecisionExpiryService
    {
        public int Calls;
        public int ToReturn;

        public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToReturn);
        }
    }
}
