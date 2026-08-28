using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// A team admin choosing the deployment's default destination for one data class.
///
/// <para>Team scope, and deliberately so: the deployment authors the template, but only this team can decide to be
/// taken off the storage it has now — for a class with a local home that decision is permanent, because an Active
/// route can never return to Draft and Retired is terminal.</para>
/// </summary>
public sealed record AdoptStorageDefaultCommand : ICommand<StorageAdoptionResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;

    /// <summary>The routed data class to adopt the deployment default for, for example <c>agent-run-log/v1</c>.</summary>
    public required string DataClassTypeKey { get; init; }
}
