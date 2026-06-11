using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>Projects the in-memory <see cref="IAgentHarnessRegistry"/> into the wire DTO — a pure registry read, no DB.</summary>
public sealed class ListHarnessesQueryHandler : IRequestHandler<ListHarnessesQuery, IReadOnlyList<HarnessSummary>>
{
    private readonly IAgentHarnessRegistry _registry;

    public ListHarnessesQueryHandler(IAgentHarnessRegistry registry) { _registry = registry; }

    public Task<IReadOnlyList<HarnessSummary>> Handle(ListHarnessesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<HarnessSummary> harnesses = _registry.All
            .Select(h => new HarnessSummary { Kind = h.Kind, Version = h.Version, Models = h.Models })
            .ToList();

        return Task.FromResult(harnesses);
    }
}
