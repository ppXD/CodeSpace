using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Commands.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: the A4 scorecard-backfill job is the standard job → command → service chain (Rule 14 + Rule 16) — the
/// job holds no query and no projection, the handler holds no logic. Hand-rolled recording doubles, matching the
/// codebase convention.
/// </summary>
[Trait("Category", "Unit")]
public class RunScorecardBackfillDispatchTests
{
    [Fact]
    public async Task The_backfill_job_dispatches_its_command_hourly_and_holds_no_logic()
    {
        var mediator = new RecordingMediator();
        var job = new RunScorecardBackfillRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(RunScorecardBackfillRecurringJob));
        job.CronExpression.ShouldBe("17 * * * *", "the catch-up runs off the hour so it never contends with the on-the-hour sweeps");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<BackfillRunScorecardsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_backfill_handler_forwards_the_batch_size_and_returns_the_written_count()
    {
        var backfill = new RecordingBackfill { ToReturn = 12 };
        var handler = new BackfillRunScorecardsCommandHandler(backfill);

        var written = await handler.Handle(new BackfillRunScorecardsCommand { BatchSize = 25 }, CancellationToken.None);

        backfill.BatchSizes.ShouldHaveSingleItem().ShouldBe(25, "the handler delegates the whole job to the service (Rule 16)");
        written.ShouldBe(12, "the handler surfaces the service's written count verbatim");
    }

    [Fact]
    public void The_command_defaults_to_a_bounded_batch()
    {
        new BackfillRunScorecardsCommand().BatchSize.ShouldBe(50, "an unbounded catch-up pass would let one tick sweep the whole run history");
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

    /// <summary>Records the batch sizes asked for + returns a canned count, so the handler test asserts pure delegation.</summary>
    private sealed class RecordingBackfill : IRunScorecardBackfillService
    {
        public List<int> BatchSizes { get; } = new();
        public int ToReturn;

        public Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batchSize);
            return Task.FromResult(ToReturn);
        }
    }
}
