using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

/// <summary>
/// Lists the storage provider implementations available in this build. The catalog is process-wide, but discovery
/// is an authenticated product surface and therefore requires the caller's X-Team-Id membership context.
/// </summary>
public sealed record ListStorageProviderModulesQuery : IQuery<IReadOnlyList<StorageProviderModuleDescriptor>>, IRequireTeamMembership;
