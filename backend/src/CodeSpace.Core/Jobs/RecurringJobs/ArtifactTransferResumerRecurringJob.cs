using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Gives the transfer saga the loop it never had.
///
/// <para>Five non-terminal states and no driver meant a worker that died mid-transfer parked its intent forever, with
/// the uploaded bytes on the destination and nothing in <c>artifact_location</c> naming them. Every fifth minute
/// because the wait is pure loss — the bytes are already paid for and no reader can reach them — while the claim's own
/// lease, minutes long and renewed throughout, is what keeps a LIVE worker from ever looking abandoned. Thin Mediator
/// dispatcher (Rule 14); the work lives in
/// <see cref="Services.Workflows.Artifacts.Runtime.IArtifactCasTransferResumer"/>.</para>
/// </summary>
public sealed class ArtifactTransferResumerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ArtifactTransferResumerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ArtifactTransferResumerRecurringJob);
    public string CronExpression => "*/5 * * * *";   // every 5 minutes

    public async Task Execute() => await _mediator.Send(new ResumeAbandonedArtifactTransfersCommand()).ConfigureAwait(false);
}
