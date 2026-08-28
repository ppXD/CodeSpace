using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

/// <summary>
/// The same installed-provider catalog as <see cref="ListStorageProviderModulesQuery"/>, under the DEPLOYMENT
/// capability instead of team membership.
///
/// <para>Not a duplicate for its own sake: authoring a template is instance work, and an operator who holds
/// <c>storage.defaults.manage</c> need not belong to any team. Reaching the team-scoped list from the admin screen
/// would make the catalog unreadable for exactly the person the screen is for — and would send that screen through a
/// controller where the ambient <c>X-Team-Id</c> header is live.</para>
/// </summary>
public sealed record ListStorageDefaultProviderModulesQuery : IQuery<IReadOnlyList<StorageProviderModuleDescriptor>>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
}

/// <summary>
/// The routed data classes this build knows, under the DEPLOYMENT capability — the set an operator picks from when
/// authoring a template. Mirrors <see cref="ListRoutedDataClassesQuery"/> for the same reason its provider sibling
/// mirrors the team-scoped catalog: the operator need not belong to any team.
/// </summary>
public sealed record ListStorageDefaultDataClassesQuery : IQuery<IReadOnlyList<RoutedDataClassDescriptor>>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
}
