using CodeSpace.Core.Handlers.CommandHandlers.Auth;
using CodeSpace.Core.Handlers.CommandHandlers.Credentials;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Commands.OAuth;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Jobs;

/// <summary>
/// 🟢 Unit: the two periodic janitors that used to be <c>BackgroundService</c>s are now the standard
/// job → command → service chain (Rule 14 + Rule 16). Each job sends exactly its command on its cadence and holds no
/// logic; each handler forwards to its service and returns its count and holds no logic. Hand-rolled recording
/// doubles (no mocking lib, matching the codebase convention).
/// </summary>
[Trait("Category", "Unit")]
public class PeriodicJanitorDispatchTests
{
    [Fact]
    public async Task The_oauth_state_janitor_dispatches_its_command_every_five_minutes()
    {
        var mediator = new RecordingMediator();
        var job = new OAuthStateCleanupRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(OAuthStateCleanupRecurringJob));
        job.CronExpression.ShouldBe("*/5 * * * *", "the sweep keeps the 5-minute cadence the hosted service ran on");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<CleanupExpiredOAuthStatesCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_oauth_state_handler_forwards_to_the_cleanup_service_and_returns_its_count()
    {
        var cleanup = new RecordingStateCleanup { ToReturn = 7 };
        var handler = new CleanupExpiredOAuthStatesCommandHandler(cleanup);

        var result = await handler.Handle(new CleanupExpiredOAuthStatesCommand(), CancellationToken.None);

        cleanup.Calls.ShouldBe(1, "the handler delegates the whole job to the service (Rule 16)");
        result.Deleted.ShouldBe(7, "the handler surfaces the service's deleted count verbatim");
    }

    [Fact]
    public async Task The_unrotated_password_auditor_dispatches_its_command_every_thirty_minutes()
    {
        var mediator = new RecordingMediator();
        var job = new UnrotatedBootstrapPasswordWarningRecurringJob(mediator);

        job.JobId.ShouldBe(nameof(UnrotatedBootstrapPasswordWarningRecurringJob));
        job.CronExpression.ShouldBe("*/30 * * * *", "the audit keeps the 30-minute cadence the hosted service ran on");

        await job.Execute();

        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<WarnUnrotatedBootstrapPasswordsCommand>("the job is a thin dispatcher — it only sends the command");
    }

    [Fact]
    public async Task The_unrotated_password_handler_forwards_to_the_audit_and_returns_its_count()
    {
        var audit = new RecordingPasswordAudit { ToReturn = 2 };
        var handler = new WarnUnrotatedBootstrapPasswordsCommandHandler(audit);

        var result = await handler.Handle(new WarnUnrotatedBootstrapPasswordsCommand(), CancellationToken.None);

        audit.Calls.ShouldBe(1, "the handler delegates the whole job to the service (Rule 16)");
        result.Unrotated.ShouldBe(2, "the handler surfaces the audit's unrotated count verbatim");
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

    /// <summary>Records the sweep call + returns a canned count, so the handler test asserts pure delegation.</summary>
    private sealed class RecordingStateCleanup : IOAuthStateCleanup
    {
        public int Calls;
        public int ToReturn;

        public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToReturn);
        }
    }

    /// <summary>Records the audit call + returns a canned count, so the handler test asserts pure delegation.</summary>
    private sealed class RecordingPasswordAudit : IUnrotatedBootstrapPasswordAudit
    {
        public int Calls;
        public int ToReturn;

        public Task<int> WarnUnrotatedAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToReturn);
        }
    }
}
