using CodeSpace.Messages.Dtos.Teams;

namespace CodeSpace.Core.Services.Teams;

/// <summary>Opening a new workspace. The creator owns it.</summary>
public interface ITeamProvisioningService
{
    Task<TeamSummary> CreateAsync(string name, CancellationToken cancellationToken);
}
