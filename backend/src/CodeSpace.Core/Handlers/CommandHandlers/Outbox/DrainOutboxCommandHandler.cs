using CodeSpace.Core.Services.Outbox;
using CodeSpace.Messages.Commands.Outbox;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Outbox;

/// <summary>
/// Rule 16 thin handler — delegates to <see cref="IOutboxDispatcher.DrainOnceAsync"/> and
/// wraps the count into a response DTO.
/// </summary>
public sealed class DrainOutboxCommandHandler : IRequestHandler<DrainOutboxCommand, DrainOutboxResponse>
{
    private readonly IOutboxDispatcher _dispatcher;

    public DrainOutboxCommandHandler(IOutboxDispatcher dispatcher) { _dispatcher = dispatcher; }

    public async Task<DrainOutboxResponse> Handle(DrainOutboxCommand request, CancellationToken cancellationToken)
    {
        var processed = await _dispatcher.DrainOnceAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
        return new DrainOutboxResponse { Processed = processed };
    }
}
