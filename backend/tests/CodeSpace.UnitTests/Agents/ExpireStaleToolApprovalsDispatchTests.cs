using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the D3 dispatch chain stays thin (Rule 14 + Rule 16). The recurring job sends exactly the
/// <see cref="ExpireStaleToolApprovalsCommand"/> on a minutely cadence (no logic of its own), and the command handler
/// forwards to <see cref="IToolApprovalExpiryService"/> and returns its count (no logic of its own). Hand-rolled
/// recording doubles (no mocking lib, matching the codebase convention).
/// </summary>
[Trait("Category", "Unit")]
public class ExpireStaleToolApprovalsDispatchTests
{
    [Fact]
    public async Task The_recurring_job_dispatches_the_expire_command_minutely()
    {
        var mediator = new RecordingMediator();
        var job = new ExpireStaleToolApprovalsRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(ExpireStaleToolApprovalsRecurringJob));
        job.CronExpression.ShouldBe(Cron.Minutely(), "the reaper runs every minute — responsive + cheap");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ExpireStaleToolApprovalsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_handler_forwards_to_the_expiry_service_and_returns_its_count()
    {
        var service = new RecordingExpiryService { ToReturn = 4 };
        var handler = new ExpireStaleToolApprovalsCommandHandler(service);

        var result = await handler.Handle(new ExpireStaleToolApprovalsCommand(), CancellationToken.None);

        service.Calls.ShouldBe(1, "the handler delegates the whole job to the service (Rule 16)");
        result.Expired.ShouldBe(4, "the handler surfaces the service's expired count verbatim");
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

    /// <summary>Records the expiry call + returns a canned count, so the handler test asserts pure delegation.</summary>
    private sealed class RecordingExpiryService : IToolApprovalExpiryService
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
