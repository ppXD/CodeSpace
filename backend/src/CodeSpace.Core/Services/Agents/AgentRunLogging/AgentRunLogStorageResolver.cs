using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Deterministic team policy for the first producer slice: an explicitly named <c>agent-run-logs</c> Active profile
/// wins; otherwise exactly one Active profile is unambiguous. Zero or multiple candidates fail visibly instead of
/// silently choosing a destination whose retention/security posture the operator did not authorize.
/// </summary>
public sealed class AgentRunLogStorageResolver : IAgentRunLogStorageResolver
{
    public const string ReservedStableName = "agent-run-logs";
    private readonly CodeSpaceDbContext _db;

    public AgentRunLogStorageResolver(CodeSpaceDbContext db) => _db = db;

    public async Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty) return new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing);
        var reserved = await _db.StorageProfile.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.StableName == ReservedStableName && value.State == StorageProfileState.Active)
            .Select(value => new { value.Id, value.CurrentRevision }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (reserved != null) return new AgentRunLogStorageResolution.Ready(reserved.Id, reserved.CurrentRevision);

        var candidates = await _db.StorageProfile.AsNoTracking().Where(value => value.TeamId == teamId && value.State == StorageProfileState.Active)
            .OrderBy(value => value.StableName).ThenBy(value => value.Id).Select(value => new { value.Id, value.CurrentRevision }).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count switch
        {
            1 => new AgentRunLogStorageResolution.Ready(candidates[0].Id, candidates[0].CurrentRevision),
            0 => new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing),
            _ => new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Ambiguous),
        };
    }
}
