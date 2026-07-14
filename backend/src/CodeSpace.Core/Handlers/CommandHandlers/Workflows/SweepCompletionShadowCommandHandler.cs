using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class SweepCompletionShadowCommandHandler : IRequestHandler<SweepCompletionShadowCommand, int>
{
    private readonly ICompletionShadowService _shadow;

    public SweepCompletionShadowCommandHandler(ICompletionShadowService shadow) { _shadow = shadow; }

    public async Task<int> Handle(SweepCompletionShadowCommand request, CancellationToken cancellationToken) =>
        await _shadow.SweepAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
}
