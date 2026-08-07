using CodeSpace.Messages.Commands.OAuth;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 5 minutes, dispatches <see cref="CleanupExpiredOAuthStatesCommand"/> to sweep expired
/// <c>oauth_pending_state</c> rows. Thin Mediator dispatcher (Rule 14) — the delete lives in
/// <see cref="Services.OAuth.IOAuthStateCleanup"/>.
///
/// <para>Replaces a <c>BackgroundService</c> that ran its own <c>PeriodicTimer</c> on EVERY pod. As a recurring job
/// it runs only where a Hangfire server does (the Worker role), inside the mediator pipeline, and is visible +
/// manually triggerable on the dashboard. The one behavioural difference: the old service swept once at startup, so
/// after a long outage rows now linger for up to one cron interval instead of being reclaimed immediately — 5
/// minutes on a janitor whose rows are already past their TTL.</para>
/// </summary>
public sealed class OAuthStateCleanupRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public OAuthStateCleanupRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(OAuthStateCleanupRecurringJob);
    public string CronExpression => "*/5 * * * *";   // every 5 minutes

    public async Task Execute() => await _mediator.Send(new CleanupExpiredOAuthStatesCommand()).ConfigureAwait(false);
}
