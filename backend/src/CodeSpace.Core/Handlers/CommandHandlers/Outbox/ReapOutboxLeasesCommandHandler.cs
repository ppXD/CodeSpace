using CodeSpace.Core.Services.Outbox;
using CodeSpace.Messages.Commands.Outbox;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Outbox;

/// <summary>
/// Rule 16 thin handler — delegates to <see cref="IOutboxLeaseReaper.ReapAsync"/> and wraps
/// the count into a response DTO.
/// </summary>
public sealed class ReapOutboxLeasesCommandHandler : IRequestHandler<ReapOutboxLeasesCommand, ReapOutboxLeasesResponse>
{
    private readonly IOutboxLeaseReaper _reaper;

    public ReapOutboxLeasesCommandHandler(IOutboxLeaseReaper reaper) { _reaper = reaper; }

    public async Task<ReapOutboxLeasesResponse> Handle(ReapOutboxLeasesCommand request, CancellationToken cancellationToken)
    {
        var reaped = await _reaper.ReapAsync(cancellationToken).ConfigureAwait(false);
        return new ReapOutboxLeasesResponse { Reaped = reaped };
    }
}
