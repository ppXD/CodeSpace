using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 5 minutes, dispatches <see cref="PurgeExpiredWebhookClaimsCommand"/> to clear expired <c>webhook_claim</c> rows.
/// Thin Mediator dispatcher (Rule 14) — the delete lives in <see cref="Services.Webhooks.IWebhookClaimStore"/>.
///
/// <para>Replaces a bounded purge each claim used to run inside its own delivery's transaction. That held the purged
/// rows' locks until the delivery committed, and two deliveries that each purged a batch and then claimed a key in the
/// other's batch deadlocked — one of them failing with a 5xx the provider does not redeliver on its own.</para>
/// </summary>
public sealed class WebhookClaimPurgeRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public WebhookClaimPurgeRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(WebhookClaimPurgeRecurringJob);
    public string CronExpression => "*/5 * * * *";   // every 5 minutes

    public async Task Execute() => await _mediator.Send(new PurgeExpiredWebhookClaimsCommand()).ConfigureAwait(false);
}
