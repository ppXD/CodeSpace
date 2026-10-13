using CodeSpace.Core.Handlers.CommandHandlers.Storage;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: the storage namespace fingerprint backfill is the standard job → command → service chain (Rule 14 + Rule
/// 16) — the job holds no query, the handler holds no logic. Hand-rolled recording doubles, matching the codebase
/// convention.
/// </summary>
[Trait("Category", "Unit")]
public class StorageNamespaceFingerprintBackfillDispatchTests
{
    [Fact]
    public async Task The_backfill_job_dispatches_its_command_every_ten_minutes_and_holds_no_logic()
    {
        var mediator = new RecordingMediator();
        var job = new StorageNamespaceFingerprintBackfillRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(StorageNamespaceFingerprintBackfillRecurringJob), "Hangfire indexes the schedule by this id — a rename strands the old one");
        job.CronExpression.ShouldBe("*/10 * * * *", "a committed cadence: a revision an older pod stamps during the rollout is restored within ten minutes");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<BackfillStorageNamespaceFingerprintsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_backfill_handler_forwards_the_batch_size_and_returns_the_restored_count()
    {
        var backfill = new RecordingBackfill { ToReturn = 3 };
        var handler = new BackfillStorageNamespaceFingerprintsCommandHandler(backfill);

        var restored = await handler.Handle(new BackfillStorageNamespaceFingerprintsCommand { BatchSize = 7 }, CancellationToken.None);

        backfill.BatchSizes.ShouldHaveSingleItem().ShouldBe(7, "the handler delegates the whole pass to the service (Rule 16)");
        restored.ShouldBe(3, "the handler surfaces the service's count verbatim");
    }

    [Fact]
    public void The_backfill_command_is_bounded_and_owns_no_command_transaction()
    {
        new BackfillStorageNamespaceFingerprintsCommand().BatchSize.ShouldBe(200, "an unbounded pass would hold the revision table's write lock for as long as the backlog is long");

        typeof(INonTransactionalCommand).IsAssignableFrom(typeof(BackfillStorageNamespaceFingerprintsCommand))
            .ShouldBeTrue("the service opens the one transaction its trigger switch must live inside; a command transaction around it would refuse a second");
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
    private sealed class RecordingBackfill : IStorageNamespaceFingerprintBackfillService
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
