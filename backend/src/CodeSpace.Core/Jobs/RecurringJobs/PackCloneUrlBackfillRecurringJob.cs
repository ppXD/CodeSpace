using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every ten minutes: seal the clone URL of any pack row that still holds a pasted token in plaintext — the rows imported before the seal existed, and any an older pod writes during a rolling deploy (thin Rule-14 dispatcher). A sealed row is no longer a candidate, so an idle tick is one small read.</summary>
public sealed class PackCloneUrlBackfillRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public PackCloneUrlBackfillRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(PackCloneUrlBackfillRecurringJob);
    public string CronExpression => "*/10 * * * *";

    public async Task Execute() => await _mediator.Send(new BackfillPackCloneUrlsCommand()).ConfigureAwait(false);
}
