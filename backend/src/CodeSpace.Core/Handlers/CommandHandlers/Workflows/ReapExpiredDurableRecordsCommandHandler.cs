using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Rule 16 — thin handler. The bounded sweep lives in <see cref="IDurableRetentionReaper"/>.</summary>
public sealed class ReapExpiredDurableRecordsCommandHandler : IRequestHandler<ReapExpiredDurableRecordsCommand, ReapExpiredDurableRecordsResponse>
{
    private readonly IDurableRetentionReaper _reaper;

    public ReapExpiredDurableRecordsCommandHandler(IDurableRetentionReaper reaper) { _reaper = reaper; }

    public async Task<ReapExpiredDurableRecordsResponse> Handle(ReapExpiredDurableRecordsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _reaper.SweepAsync(cancellationToken).ConfigureAwait(false);

        return new ReapExpiredDurableRecordsResponse { Summary = summary };
    }
}
