using CodeSpace.Core.Handlers.CommandHandlers.Credentials;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: shredding the secrets an older build retired without blanking them is the standard job → command → service
/// chain (Rule 14 + Rule 16) — the job holds no query, the handler holds no logic. Hand-rolled recording doubles, matching
/// the codebase convention.
/// </summary>
[Trait("Category", "Unit")]
public class RetiredSecretShredDispatchTests
{
    [Fact]
    public async Task The_shred_job_dispatches_its_command_every_hour_and_holds_no_logic()
    {
        var mediator = new RecordingMediator();
        var job = new RetiredSecretShredRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(RetiredSecretShredRecurringJob), "Hangfire indexes the schedule by this id — a rename strands the old one");
        job.CronExpression.ShouldBe("0 * * * *", "a committed cadence: a secret an older pod retires during a rollout stays decryptable at most an hour after the new build takes over");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ShredRetiredSecretsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_shred_handler_forwards_to_the_service_and_returns_its_count()
    {
        var shred = new RecordingShred { ToReturn = 4 };
        var handler = new ShredRetiredSecretsCommandHandler(shred);

        var shredded = await handler.Handle(new ShredRetiredSecretsCommand(), CancellationToken.None);

        shred.Calls.ShouldBe(1, "the handler delegates the whole pass to the service (Rule 16)");
        shredded.ShouldBe(4, "the handler surfaces the service's count verbatim");
    }

    [Fact]
    public void The_shred_command_settles_each_kind_of_secret_on_its_own()
    {
        typeof(INonTransactionalCommand).IsAssignableFrom(typeof(ShredRetiredSecretsCommand))
            .ShouldBeTrue("each kind is blanked by one statement that commits on its own; one transaction around the pass would hold every candidate row's lock until the last statement and let one failing statement undo the rest");
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

    /// <summary>Counts the passes asked for + returns a canned count, so the handler test asserts pure delegation.</summary>
    private sealed class RecordingShred : IRetiredSecretShredService
    {
        public int Calls;
        public int ToReturn;

        public Task<int> ShredAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToReturn);
        }
    }
}
