using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Probe the provider through the repository's bound credential. <see cref="TeamPermissions.ReposManage"/>: the probe
/// spends that credential against the provider, which is a decision for whoever manages the binding.
/// </summary>
public sealed record TestRepositoryBindingCommand : ICommand<CredentialProbeResult>, IRequireRepositoryAccess, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ReposManage;

    public required Guid RepositoryId { get; init; }
}
