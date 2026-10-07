using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: the pack clone-URL backfill is the standard job → command → service chain (Rule 14 + Rule 16) — the job
/// holds no query, the handler holds no logic — and the import-from-pack command is gated like every other pack write.
/// Hand-rolled recording doubles, matching the codebase convention.
/// </summary>
[Trait("Category", "Unit")]
public class PackCloneUrlBackfillDispatchTests
{
    [Fact]
    public async Task The_backfill_job_dispatches_its_command_every_ten_minutes_and_holds_no_logic()
    {
        var mediator = new RecordingMediator();
        var job = new PackCloneUrlBackfillRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(PackCloneUrlBackfillRecurringJob), "Hangfire indexes the schedule by this id — a rename strands the old one");
        job.CronExpression.ShouldBe("*/10 * * * *", "a committed cadence: plaintext tokens left by an older pod are sealed within ten minutes");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<BackfillPackCloneUrlsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_backfill_handler_forwards_the_batch_size_and_returns_the_sealed_count()
    {
        var backfill = new RecordingBackfill { ToReturn = 3 };
        var handler = new BackfillPackCloneUrlsCommandHandler(backfill);

        var sealedCount = await handler.Handle(new BackfillPackCloneUrlsCommand { BatchSize = 7 }, CancellationToken.None);

        backfill.BatchSizes.ShouldHaveSingleItem().ShouldBe(7, "the handler delegates the whole pass to the service (Rule 16)");
        sealedCount.ShouldBe(3, "the handler surfaces the service's count verbatim");
    }

    [Fact]
    public void The_backfill_command_is_bounded_and_settles_each_pack_on_its_own()
    {
        new BackfillPackCloneUrlsCommand().BatchSize.ShouldBe(50, "an unbounded pass would let one tick sweep every pack in the deployment");

        typeof(INonTransactionalCommand).IsAssignableFrom(typeof(BackfillPackCloneUrlsCommand))
            .ShouldBeTrue("each pack is sealed by its own conditional UPDATE; one transaction around the pass would let one failing pack undo the rest");
    }

    [Fact]
    public void Importing_from_a_saved_pack_needs_the_same_permission_as_importing_from_a_url()
    {
        new ImportPackArtifactsCommand().RequiredPermission.ShouldBe(TeamPermissions.AgentsWrite, "it spends the pack's saved credential, exactly like Sync");
        new ImportPackArtifactsCommand().RequiredPermission.ShouldBe(new ImportPackFromUrlCommand { Url = "https://github.com/acme/agents" }.RequiredPermission);
        new ImportPackArtifactsCommand().RequiredPermission.ShouldBe(new SyncPackCommand { PackId = Guid.NewGuid() }.RequiredPermission);
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
    private sealed class RecordingBackfill : IPackCloneUrlBackfillService
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
