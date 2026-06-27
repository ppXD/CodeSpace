using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// EF-backed <see cref="IAgentSkillBindingService"/>. The join is the single source of truth for which skills
/// an agent carries; reads join through to the active definitions (so a soft-deleted skill/agent silently
/// drops out of both directions without touching binding rows). <see cref="SetForAgentAsync"/> validates
/// tenancy on both sides, then applies the minimal add/remove diff so a no-op save writes nothing.
/// </summary>
public sealed class AgentSkillBindingService : IAgentSkillBindingService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentSkillBindingService> _logger;

    public AgentSkillBindingService(CodeSpaceDbContext db, ILogger<AgentSkillBindingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SkillDefinitionSummary>> ListForAgentAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        await EnsureAgentInTeamAsync(teamId, agentDefinitionId, cancellationToken).ConfigureAwait(false);

        return await _db.AgentSkillBinding.AsNoTracking()
            .Where(b => b.AgentDefinitionId == agentDefinitionId)
            .Join(ActiveSkills(teamId), b => b.SkillDefinitionId, s => s.Id, (b, s) => new { b.CreatedDate, s })
            .OrderBy(x => x.CreatedDate)
            .Select(x => new SkillDefinitionSummary
            {
                Id = x.s.Id,
                TeamId = x.s.TeamId,
                Slug = x.s.Slug,
                Name = x.s.Name,
                Description = x.s.Description,
                Category = x.s.Category,
                Origin = x.s.Origin,
                PackId = x.s.PackId,
                CreatedDate = x.s.CreatedDate,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentDefinitionSummary>> ListAgentsForSkillAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken)
    {
        await EnsureSkillInTeamAsync(teamId, skillDefinitionId, cancellationToken).ConfigureAwait(false);

        var rows = await _db.AgentSkillBinding.AsNoTracking()
            .Where(b => b.SkillDefinitionId == skillDefinitionId)
            .Join(ActiveAgents(teamId), b => b.AgentDefinitionId, a => a.Id, (b, a) => new { b.CreatedDate, a })
            .OrderBy(x => x.CreatedDate)
            .Select(x => new { x.a.Id, x.a.TeamId, x.a.Slug, x.a.Name, x.a.Description, x.a.SystemPrompt, x.a.Model, x.a.DefaultAutonomy, x.a.ToolsJson, x.a.Origin, x.a.CreatedDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new AgentDefinitionSummary
        {
            Id = r.Id,
            TeamId = r.TeamId,
            Slug = r.Slug,
            Name = r.Name,
            Description = r.Description,
            SystemPrompt = r.SystemPrompt,
            Model = r.Model,
            DefaultAutonomy = r.DefaultAutonomy,
            Tools = string.IsNullOrWhiteSpace(r.ToolsJson) ? null : JsonSerializer.Deserialize<List<string>>(r.ToolsJson),
            Origin = r.Origin,
            CreatedDate = r.CreatedDate,
        }).ToList();
    }

    public async Task SetForAgentAsync(Guid teamId, Guid agentDefinitionId, IReadOnlyList<Guid> skillDefinitionIds, Guid actorUserId, CancellationToken cancellationToken)
    {
        await EnsureAgentInTeamAsync(teamId, agentDefinitionId, cancellationToken).ConfigureAwait(false);

        var desired = skillDefinitionIds.Distinct().ToHashSet();
        await EnsureSkillsInTeamAsync(teamId, desired, cancellationToken).ConfigureAwait(false);

        var existing = await _db.AgentSkillBinding
            .Where(b => b.AgentDefinitionId == agentDefinitionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var existingIds = existing.Select(b => b.SkillDefinitionId).ToHashSet();

        var toRemove = existing.Where(b => !desired.Contains(b.SkillDefinitionId)).ToList();
        var toAdd = desired.Where(id => !existingIds.Contains(id)).ToList();

        if (toRemove.Count == 0 && toAdd.Count == 0) return;

        _db.AgentSkillBinding.RemoveRange(toRemove);

        var now = DateTimeOffset.UtcNow;
        foreach (var skillId in toAdd)
            _db.AgentSkillBinding.Add(new AgentSkillBinding { Id = Guid.NewGuid(), AgentDefinitionId = agentDefinitionId, SkillDefinitionId = skillId, CreatedDate = now, CreatedBy = actorUserId });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent skills set: team={TeamId} agent={AgentId} added={Added} removed={Removed}", teamId, agentDefinitionId, toAdd.Count, toRemove.Count);
    }

    private IQueryable<SkillDefinition> ActiveSkills(Guid teamId) =>
        _db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && s.DeletedDate == null);

    private IQueryable<AgentDefinition> ActiveAgents(Guid teamId) =>
        _db.AgentDefinition.AsNoTracking().Where(a => a.TeamId == teamId && a.DeletedDate == null);

    private async Task EnsureAgentInTeamAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var ok = await _db.AgentDefinition.AsNoTracking()
            .AnyAsync(a => a.Id == agentDefinitionId && a.TeamId == teamId && a.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!ok) throw new KeyNotFoundException($"Agent persona {agentDefinitionId} not found or not accessible.");
    }

    private async Task EnsureSkillInTeamAsync(Guid teamId, Guid skillDefinitionId, CancellationToken cancellationToken)
    {
        var ok = await _db.SkillDefinition.AsNoTracking()
            .AnyAsync(s => s.Id == skillDefinitionId && s.TeamId == teamId && s.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!ok) throw new KeyNotFoundException($"Skill {skillDefinitionId} not found or not accessible.");
    }

    private async Task EnsureSkillsInTeamAsync(Guid teamId, IReadOnlyCollection<Guid> skillIds, CancellationToken cancellationToken)
    {
        if (skillIds.Count == 0) return;

        var found = await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.DeletedDate == null && skillIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var missing = skillIds.Except(found).ToList();

        if (missing.Count > 0)
            throw new KeyNotFoundException($"Skill(s) not found or not accessible in this team: {string.Join(", ", missing)}");
    }
}
