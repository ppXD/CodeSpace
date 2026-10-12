using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every five minutes: retire the personal tokens still live in a team their person no longer has standing in — every departure from before departures retired them, any an older pod serves during a rolling deploy, and an instance Admin's in a team they are not in once they lose the role (thin Rule-14 dispatcher). A retired token is no longer a candidate, so an idle tick is one small read.</summary>
public sealed class DepartedMemberCredentialRetirementRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public DepartedMemberCredentialRetirementRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(DepartedMemberCredentialRetirementRecurringJob);
    public string CronExpression => "*/5 * * * *";

    public async Task Execute() => await _mediator.Send(new RetireDepartedMembersCredentialsCommand()).ConfigureAwait(false);
}
