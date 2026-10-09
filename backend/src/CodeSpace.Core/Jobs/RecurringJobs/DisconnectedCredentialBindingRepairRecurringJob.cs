using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every five minutes: move the repositories and group hooks still bound to a disconnected credential onto its owner's later reconnect — the rows stranded before the connect paths carried them forward, and any an older pod strands during a rolling deploy (thin Rule-14 dispatcher). A moved binding is no longer a candidate, so an idle tick is one small read.</summary>
public sealed class DisconnectedCredentialBindingRepairRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public DisconnectedCredentialBindingRepairRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(DisconnectedCredentialBindingRepairRecurringJob);
    public string CronExpression => "*/5 * * * *";

    public async Task Execute() => await _mediator.Send(new RepairDisconnectedCredentialBindingsCommand()).ConfigureAwait(false);
}
