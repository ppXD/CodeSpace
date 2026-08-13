using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Teams;

namespace CodeSpace.Core.Services.Teams;

/// <summary>Bringing a team into existence — both kinds. The owner is recorded as an Owner membership row.</summary>
public interface ITeamProvisioningService
{
    /// <summary>Opens a workspace for the caller, saved.</summary>
    Task<TeamSummary> CreateAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stages the personal workspace an account is created with, WITHOUT saving — the account it
    /// belongs to is still being built in the same unit of work, and flushing here would write half
    /// of it.
    /// </summary>
    Task<Team> StagePersonalAsync(Guid ownerUserId, CancellationToken cancellationToken);
}
