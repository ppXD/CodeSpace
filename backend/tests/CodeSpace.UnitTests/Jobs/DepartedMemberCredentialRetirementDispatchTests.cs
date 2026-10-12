using CodeSpace.Core.Handlers.CommandHandlers.Credentials;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: retiring the tokens of people no longer in the team they lent them to is the standard job → command → service
/// chain (Rule 14 + Rule 16) — the job holds no query, the handler holds no logic. Hand-rolled recording doubles, matching
/// the codebase convention.
/// </summary>
[Trait("Category", "Unit")]
public class DepartedMemberCredentialRetirementDispatchTests
{
    [Fact]
    public async Task The_retirement_job_dispatches_its_command_every_five_minutes_and_holds_no_logic()
    {
        var mediator = new RecordingMediator();
        var job = new DepartedMemberCredentialRetirementRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(DepartedMemberCredentialRetirementRecurringJob), "Hangfire indexes the schedule by this id — a rename strands the old one");
        job.CronExpression.ShouldBe("*/5 * * * *", "a committed cadence: a token left behind by a departure an older pod served stops being the team's within five minutes");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<RetireDepartedMembersCredentialsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_retirement_handler_forwards_the_batch_size_and_returns_the_retired_count()
    {
        var revocation = new RecordingRevocation { ToReturn = 3 };
        var handler = new RetireDepartedMembersCredentialsCommandHandler(revocation);

        var retired = await handler.Handle(new RetireDepartedMembersCredentialsCommand { BatchSize = 7 }, CancellationToken.None);

        revocation.BatchSizes.ShouldHaveSingleItem().ShouldBe(7, "the handler delegates the whole pass to the service (Rule 16)");
        retired.ShouldBe(3, "the handler surfaces the service's count verbatim");
    }

    [Fact]
    public void The_retirement_command_is_bounded_and_settles_each_credential_on_its_own()
    {
        new RetireDepartedMembersCredentialsCommand().BatchSize.ShouldBe(50, "an unbounded pass would let one tick retire every orphaned token in the deployment in one go");

        typeof(INonTransactionalCommand).IsAssignableFrom(typeof(RetireDepartedMembersCredentialsCommand))
            .ShouldBeTrue("each credential is retired and saved on its own; one transaction around the pass would let one failing credential undo the rest");
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
    private sealed class RecordingRevocation : ICredentialRevocationService
    {
        public List<int> BatchSizes { get; } = new();
        public int ToReturn;

        public Task<int> CascadeAsync(Guid credentialId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RetireAsync(Credential credential, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetireDepartedInTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RetireDepartedAsync(int batchSize, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batchSize);
            return Task.FromResult(ToReturn);
        }
    }
}
