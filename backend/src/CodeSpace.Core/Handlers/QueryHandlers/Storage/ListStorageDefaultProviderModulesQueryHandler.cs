using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

/// <summary>Delegates to the team-scoped handler's own projection, so the two catalogs cannot describe the same modules differently.</summary>
public sealed class ListStorageDefaultProviderModulesQueryHandler : IRequestHandler<ListStorageDefaultProviderModulesQuery, IReadOnlyList<StorageProviderModuleDescriptor>>
{
    private readonly ListStorageProviderModulesQueryHandler _inner;

    public ListStorageDefaultProviderModulesQueryHandler(ListStorageProviderModulesQueryHandler inner) { _inner = inner; }

    public Task<IReadOnlyList<StorageProviderModuleDescriptor>> Handle(ListStorageDefaultProviderModulesQuery request, CancellationToken cancellationToken) =>
        _inner.Handle(new ListStorageProviderModulesQuery(), cancellationToken);
}

/// <summary>Delegates to the team-scoped handler's own projection, so the two catalogs cannot describe the same classes differently.</summary>
public sealed class ListStorageDefaultDataClassesQueryHandler : IRequestHandler<ListStorageDefaultDataClassesQuery, IReadOnlyList<RoutedDataClassDescriptor>>
{
    private readonly ListRoutedDataClassesQueryHandler _inner;

    public ListStorageDefaultDataClassesQueryHandler(ListRoutedDataClassesQueryHandler inner) { _inner = inner; }

    public Task<IReadOnlyList<RoutedDataClassDescriptor>> Handle(ListStorageDefaultDataClassesQuery request, CancellationToken cancellationToken) =>
        _inner.Handle(new ListRoutedDataClassesQuery(), cancellationToken);
}
