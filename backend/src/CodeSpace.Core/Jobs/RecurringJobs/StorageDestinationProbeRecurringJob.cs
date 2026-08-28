using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Keeps the answer to "does my storage still work" no more than one tick out of date.
///
/// <para>Before this, the only probe anyone ever ran was one an operator clicked. A credential revoked at the provider
/// — the action <c>StorageCredentialService</c> itself tells operators to take — changed nothing observable: writes
/// settled green over an empty destination and reads failed only when a person happened to open an artifact.</para>
/// </summary>
public sealed class StorageDestinationProbeRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StorageDestinationProbeRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StorageDestinationProbeRecurringJob);

    /// <summary>Every 15 minutes, matching the model plane's probe cadence. The sweep's own 10-minute staleness window is deliberately shorter, so a tick never skips on clock jitter.</summary>
    public string CronExpression => "*/15 * * * *";

    public async Task Execute() => await _mediator.Send(new ProbeStaleStorageDestinationsCommand()).ConfigureAwait(false);
}
