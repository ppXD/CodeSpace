using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

/// <summary>
/// Pure catalog projection for the Settings data-route picker. It reads declaration fields only: no route, profile,
/// credential or team state, so the answer is identical for every caller in this deployment.
/// </summary>
public sealed class ListRoutedDataClassesQueryHandler : IRequestHandler<ListRoutedDataClassesQuery, IReadOnlyList<RoutedDataClassDescriptor>>
{
    private readonly IRoutedDataClassCatalog _catalog;

    public ListRoutedDataClassesQueryHandler(IRoutedDataClassCatalog catalog) { _catalog = catalog; }

    public Task<IReadOnlyList<RoutedDataClassDescriptor>> Handle(ListRoutedDataClassesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoutedDataClassDescriptor> result = _catalog.DataClasses
            .Select(dataClass => new RoutedDataClassDescriptor { TypeKey = dataClass.TypeKey, DisplayName = dataClass.DisplayName, HasLocalFallback = dataClass is IRoutedDataClassLocalFallback })
            .ToList();

        return Task.FromResult(result);
    }
}
