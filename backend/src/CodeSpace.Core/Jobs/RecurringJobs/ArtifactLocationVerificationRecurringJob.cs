using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Makes Missing and Corrupt real.
///
/// <para>Both states were declared in the schema and written by no production code, so bytes that vanished or rotted
/// at a provider after commit stayed Available forever — knowable only when a person opened that artifact and the read
/// threw. Hourly rather than by the minute: this is decay, not an outage, and the destination-health sweep already
/// answers the faster question of whether the provider is reachable at all.</para>
/// </summary>
public sealed class ArtifactLocationVerificationRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ArtifactLocationVerificationRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ArtifactLocationVerificationRecurringJob);
    public string CronExpression => "17 * * * *";   // hourly, off the hour so it does not pile onto every other sweep

    public async Task Execute() => await _mediator.Send(new VerifyStaleArtifactLocationsCommand()).ConfigureAwait(false);
}
