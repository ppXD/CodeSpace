using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IStorageNamespaceFingerprintBackfillService.BackfillAsync"/>.</summary>
public sealed class BackfillStorageNamespaceFingerprintsCommandHandler : IRequestHandler<BackfillStorageNamespaceFingerprintsCommand, int>
{
    private readonly IStorageNamespaceFingerprintBackfillService _backfill;

    public BackfillStorageNamespaceFingerprintsCommandHandler(IStorageNamespaceFingerprintBackfillService backfill)
    {
        _backfill = backfill;
    }

    public async Task<int> Handle(BackfillStorageNamespaceFingerprintsCommand request, CancellationToken cancellationToken)
    {
        return await _backfill.BackfillAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
