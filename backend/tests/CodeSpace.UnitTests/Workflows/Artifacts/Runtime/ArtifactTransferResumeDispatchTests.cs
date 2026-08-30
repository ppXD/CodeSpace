using CodeSpace.Core.Handlers.CommandHandlers.Storage;
using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The wiring that makes the recovery sweep happen at all — job to command to handler to one bounded pass.
///
/// <para>Every value pinned here is load-bearing and none of them changes any behaviour a test of the sweep itself
/// would notice. Hangfire indexes the schedule by <c>JobId</c>, so a rename strands the old entry and silently stops
/// the sweep; a cron a digit off turns five-minute recovery into five-hour; a dispatched command of the wrong type
/// misses the mediator pipeline this job exists to enter; and an unbounded batch lets one tick spend an unbounded
/// number of provider round trips.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactTransferResumeDispatchTests
{
    [Fact]
    public async Task The_recurring_job_is_a_thin_five_minutely_dispatcher_for_the_bounded_sweep()
    {
        var mediator = new RecordingMediator();
        var job = new ArtifactTransferResumerRecurringJob(mediator);

        typeof(ArtifactTransferResumerRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
        job.JobId.ShouldBe(nameof(ArtifactTransferResumerRecurringJob));
        job.CronExpression.ShouldBe("*/5 * * * *");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ResumeAbandonedArtifactTransfersCommand>();
    }

    [Fact]
    public async Task The_handler_spends_exactly_one_bounded_pass_and_reports_what_it_saw()
    {
        var resumer = new RecordingResumer();

        var response = await new ResumeAbandonedArtifactTransfersCommandHandler(resumer).Handle(new ResumeAbandonedArtifactTransfersCommand(), CancellationToken.None);

        resumer.BatchSizes.ShouldHaveSingleItem().ShouldBe(50,
            "one tick of a deployment-wide sweep must be bounded, or a backlog spends an unbounded number of provider round trips before anything else runs");

        // Distinct values on purpose: a transposed mapping would leave an operator reading a committed count where the
        // orphaned one belongs, which is the difference between "finished" and "bytes nobody can reach".
        response.Examined.ShouldBe(6);
        response.Committed.ShouldBe(5);
        response.Settled.ShouldBe(4);
        response.Orphaned.ShouldBe(3);
        response.Inconclusive.ShouldBe(2);
        response.Contended.ShouldBe(1);
    }

    private sealed class RecordingResumer : IArtifactCasTransferResumer
    {
        public List<int> BatchSizes { get; } = [];

        public Task<ArtifactTransferResumeSummary> ResumeAbandonedAsync(int batchSize, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batchSize);

            return Task.FromResult(new ArtifactTransferResumeSummary
            {
                Examined = 6, Committed = 5, Settled = 4, Orphaned = 3, Inconclusive = 2, Contended = 1,
            });
        }
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
