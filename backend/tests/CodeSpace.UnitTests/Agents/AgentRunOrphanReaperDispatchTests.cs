using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Messages.Commands.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunOrphanReaperDispatchTests
{
    [Fact]
    public async Task Recurring_job_is_the_twice_hourly_system_provenance_for_the_bounded_orphan_sweep()
    {
        var mediator = new RecordingMediator();
        var job = new AgentRunOrphanReaperRecurringJob(mediator);

        typeof(AgentRunOrphanReaperRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
        job.JobId.ShouldBe(nameof(AgentRunOrphanReaperRecurringJob));
        job.CronExpression.ShouldBe("20,50 * * * *",
            "more often than the hourly spool reaper because the netns teardown is what returns a host-global subnet lease to a BOUNDED pool, and on a minute no other sweep already uses");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ReapAgentRunOrphansCommand>();
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

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
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
