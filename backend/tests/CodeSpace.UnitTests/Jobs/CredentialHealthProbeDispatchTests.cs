using CodeSpace.Core.Handlers.CommandHandlers.Credentials;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: the credential health probe is the standard job → command → service chain (Rule 14 + Rule 16) — the job holds no
/// query and calls no provider, the handler holds no logic. Hand-rolled recording doubles, matching the codebase convention.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialHealthProbeDispatchTests
{
    [Fact]
    public async Task The_job_is_a_thin_dispatcher_of_the_probe_command_every_ten_minutes()
    {
        var mediator = new RecordingMediator();
        var job = new CredentialHealthProbeRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(CredentialHealthProbeRecurringJob), "Hangfire indexes the schedule by this id — a rename strands the old one");
        job.CronExpression.ShouldBe("*/10 * * * *");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ProbeCredentialsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_handler_hands_its_batch_size_to_the_service_and_returns_what_it_probed()
    {
        var health = new RecordingHealth { ToReturn = 3 };

        var probed = await new ProbeCredentialsCommandHandler(health).Handle(new ProbeCredentialsCommand { BatchSize = 7 }, CancellationToken.None);

        health.BatchSizes.ShouldHaveSingleItem().ShouldBe(7, "the handler delegates the whole pass to the service (Rule 16)");
        probed.ShouldBe(3);
    }

    [Fact]
    public void The_command_is_bounded_and_not_transactional()
    {
        new ProbeCredentialsCommand().BatchSize.ShouldBe(25, "an unbounded pass would let one tick call every provider for every credential in the deployment");
        typeof(INonTransactionalCommand).IsAssignableFrom(typeof(ProbeCredentialsCommand)).ShouldBeTrue("each credential is recorded on its own connection; one transaction would hold their rows across every provider call");
    }

    [Fact]
    public void The_intervals_that_decide_how_often_a_provider_is_asked_are_pinned()
    {
        CredentialHealthService.ValidatedStaleAfter.ShouldBe(TimeSpan.FromHours(6));
        CredentialHealthService.ErrorRecheckAfter.ShouldBe(TimeSpan.FromHours(1));
        CredentialHealthDeadlines.Default.Probe.ShouldBe(TimeSpan.FromSeconds(30));
        CredentialHealthDeadlines.Default.Pass.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_pass_is_over_before_the_next_tick_and_a_probe_cannot_outlast_it()
    {
        // The job fires every ten minutes; a pass that could still be running at the next tick is what piles up behind a provider that stopped answering.
        var interval = TimeSpan.FromMinutes(10);

        new CredentialHealthProbeRecurringJob(new RecordingMediator()).CronExpression.ShouldBe("*/10 * * * *", "the interval the budget is measured against");
        CredentialHealthDeadlines.Default.Pass.ShouldBeLessThan(interval);
        CredentialHealthDeadlines.Default.Probe.ShouldBeLessThan(CredentialHealthDeadlines.Default.Pass);
    }

    [Fact]
    public void The_passs_lock_key_is_pinned_because_every_worker_must_agree_on_it()
    {
        // Two workers holding different keys would run two passes at once, which is what the lock is there to prevent.
        CredentialHealthService.PassLockKey.ShouldBe(0x4352_4544_4845_4C54);
    }

    private sealed class RecordingHealth : ICredentialHealthService
    {
        public List<int> BatchSizes { get; } = new();
        public int ToReturn;

        public Task<int> ProbeBatchAsync(int batchSize, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batchSize);
            return Task.FromResult(ToReturn);
        }

        public Task ValidateNewAsync(Credential credential, ProviderInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
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
}
