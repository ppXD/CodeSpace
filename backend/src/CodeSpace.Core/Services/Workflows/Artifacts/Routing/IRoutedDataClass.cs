using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// One versioned data class that a runtime consumer in THIS build resolves through the storage routing plane. The
/// route key is an open string, so without this declaration Settings will happily accept any key that matches the
/// pattern — including the plural an operator types by hand — and the resulting route lists as configured storage that
/// nothing ever asks for.
/// </summary>
public interface IRoutedDataClass : ISingletonDependency
{
    /// <summary>The exact <c>storage_route.data_class_type_key</c> the consumer asks the routing plane for.</summary>
    string TypeKey { get; }

    /// <summary>Operator-facing name for the Settings picker.</summary>
    string DisplayName { get; }
}

/// <summary>Immutable runtime catalog of the data classes this build can route. Carries no route, profile or team state.</summary>
public interface IRoutedDataClassCatalog : ISingletonDependency
{
    /// <summary>Every routable data class, ordered by <see cref="IRoutedDataClass.TypeKey"/>.</summary>
    IReadOnlyList<IRoutedDataClass> DataClasses { get; }

    /// <summary>Returns null when no consumer in this build reads the exact key.</summary>
    IRoutedDataClass? Get(string typeKey);
}
