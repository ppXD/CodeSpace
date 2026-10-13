using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every hour: blank the secrets a build older than migration 0249 retired without blanking — any an older pod retires during a rolling deploy, and every one retired after a rollback — since 0249's own backfill ran once (thin Rule-14 dispatcher). A blanked row is no longer a candidate, so an idle tick changes nothing.</summary>
public sealed class RetiredSecretShredRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public RetiredSecretShredRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(RetiredSecretShredRecurringJob);
    public string CronExpression => "0 * * * *";

    public async Task Execute() => await _mediator.Send(new ShredRetiredSecretsCommand()).ConfigureAwait(false);
}
