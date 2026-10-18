using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every ten minutes: ask each provider whether a bounded batch of credentials still works — a personal, project or group token that expired or was revoked at GitLab is marked as needing a reconnect, and a token's real expiry is recorded, instead of every run finding out at its first call (thin Rule-14 dispatcher). A validated credential is not asked again for hours, so an idle tick is one small read.</summary>
public sealed class CredentialHealthProbeRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public CredentialHealthProbeRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(CredentialHealthProbeRecurringJob);
    public string CronExpression => "*/10 * * * *";

    public async Task Execute() => await _mediator.Send(new ProbeCredentialsCommand()).ConfigureAwait(false);
}
