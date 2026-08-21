using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class MaterializeWorkflowRunModelCallBodiesCommandHandler : IRequestHandler<MaterializeWorkflowRunModelCallBodiesCommand, int>
{
    private readonly IWorkflowRunModelCallBodyMaterializer _materializer;

    public MaterializeWorkflowRunModelCallBodiesCommandHandler(IWorkflowRunModelCallBodyMaterializer materializer) => _materializer = materializer;

    public async Task<int> Handle(MaterializeWorkflowRunModelCallBodiesCommand request, CancellationToken cancellationToken) =>
        (await _materializer.SweepAsync(request.BatchSize, cancellationToken).ConfigureAwait(false)).Settled;
}
