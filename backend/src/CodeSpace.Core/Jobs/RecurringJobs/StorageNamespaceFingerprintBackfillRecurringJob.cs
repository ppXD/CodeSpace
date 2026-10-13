using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Every ten minutes: put the namespace fingerprint the previous build compares back on any storage profile revision a physical-identity build stamped otherwise — the revisions it admitted before this release, and any an older pod admits during the rollout (thin Rule-14 dispatcher). A restored revision is no longer a candidate, so an idle tick is one small read. Deleting this class must also remove its recurring entry: see <c>StorageProfileRules.StoredNamespaceFingerprint</c>.</summary>
public sealed class StorageNamespaceFingerprintBackfillRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StorageNamespaceFingerprintBackfillRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StorageNamespaceFingerprintBackfillRecurringJob);
    public string CronExpression => "*/10 * * * *";

    public async Task Execute() => await _mediator.Send(new BackfillStorageNamespaceFingerprintsCommand()).ConfigureAwait(false);
}
