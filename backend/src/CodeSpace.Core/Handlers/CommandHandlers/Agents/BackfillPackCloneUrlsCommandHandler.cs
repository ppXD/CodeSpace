using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IPackCloneUrlBackfillService.BackfillAsync"/>.</summary>
public sealed class BackfillPackCloneUrlsCommandHandler : IRequestHandler<BackfillPackCloneUrlsCommand, int>
{
    private readonly IPackCloneUrlBackfillService _backfill;

    public BackfillPackCloneUrlsCommandHandler(IPackCloneUrlBackfillService backfill)
    {
        _backfill = backfill;
    }

    public async Task<int> Handle(BackfillPackCloneUrlsCommand request, CancellationToken cancellationToken)
    {
        return await _backfill.BackfillAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
