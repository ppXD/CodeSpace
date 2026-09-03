using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IRunScorecardBackfillService.BackfillAsync"/>.</summary>
public sealed class BackfillRunScorecardsCommandHandler : IRequestHandler<BackfillRunScorecardsCommand, int>
{
    private readonly IRunScorecardBackfillService _backfill;

    public BackfillRunScorecardsCommandHandler(IRunScorecardBackfillService backfill)
    {
        _backfill = backfill;
    }

    public async Task<int> Handle(BackfillRunScorecardsCommand request, CancellationToken cancellationToken)
    {
        return await _backfill.BackfillAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
