using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListNodeManifestsQueryHandler : IRequestHandler<ListNodeManifestsQuery, IReadOnlyList<NodeManifestDto>>
{
    private readonly IWorkflowService _service;

    public ListNodeManifestsQueryHandler(IWorkflowService service) { _service = service; }

    public Task<IReadOnlyList<NodeManifestDto>> Handle(ListNodeManifestsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_service.ListNodeManifests());
}
