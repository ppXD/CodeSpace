using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListSystemVariablesQueryHandler : IRequestHandler<ListSystemVariablesQuery, IReadOnlyList<SystemVariableDto>>
{
    private readonly IWorkflowService _service;

    public ListSystemVariablesQueryHandler(IWorkflowService service) { _service = service; }

    public Task<IReadOnlyList<SystemVariableDto>> Handle(ListSystemVariablesQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_service.ListSystemVariables());
}
