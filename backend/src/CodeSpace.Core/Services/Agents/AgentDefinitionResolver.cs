using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IAgentDefinitionResolver"/>. Loads the referenced persona team-scoped (mirroring
/// <c>RepositoryWorkspaceResolver</c>) and merges it into the task with the locked precedence:
/// <list type="bullet">
///   <item>prompt — persona system prompt PREPENDED to the node goal (the harness has one prompt conduit, the goal);</item>
///   <item>model — a non-blank node override wins, else the persona's model, else null (harness default);</item>
///   <item>everything else (harness, runner, repo, permissions, timeout) is node-authored and flows through.</item>
/// </list>
/// Tools / skills / MCP / autonomy are NOT merged yet — the task envelope + harness don't carry them, so
/// merging would silently drop them; that lands together with the harness projection in a follow-up.
/// </summary>
public sealed class AgentDefinitionResolver : IAgentDefinitionResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public AgentDefinitionResolver(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<AgentTask> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.AgentDefinitionId is not { } id) return task;   // pure-inline run — unchanged, byte-for-byte today's behavior

        var persona = await LoadPersonaAsync(id, teamId, cancellationToken).ConfigureAwait(false);

        var goal = ComposeGoal(persona.SystemPrompt, task.Goal);

        if (string.IsNullOrWhiteSpace(goal))
            throw new AgentDefinitionResolutionException(
                $"Agent persona '{persona.Slug}' has an empty system prompt and the node supplied no goal — nothing to run.");

        var model = ResolveModel(task.Model, persona.Model);

        var tools = MergeTools(ParseTools(persona.ToolsJson), task.Tools);

        return task with { Goal = goal, Model = model, Tools = tools };
    }

    private async Task<AgentDefinition> LoadPersonaAsync(Guid id, Guid teamId, CancellationToken cancellationToken) =>
        await _db.AgentDefinition.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.TeamId == teamId && a.DeletedDate == null, cancellationToken).ConfigureAwait(false)
        ?? throw new AgentDefinitionResolutionException($"Agent persona {id} not found for this team.");

    /// <summary>Persona system prompt PREPENDED to the node goal (blank-line separated); either side alone passes through.</summary>
    internal static string ComposeGoal(string systemPrompt, string goal)
    {
        var prompt = (systemPrompt ?? string.Empty).Trim();
        var task = (goal ?? string.Empty).Trim();

        if (prompt.Length == 0) return task;
        if (task.Length == 0) return prompt;

        return prompt + "\n\n" + task;
    }

    /// <summary>Node override (non-blank) wins, else the persona's model, else null = let the harness pick its default.</summary>
    internal static string? ResolveModel(string? nodeModel, string? personaModel) =>
        !string.IsNullOrWhiteSpace(nodeModel) ? nodeModel
        : !string.IsNullOrWhiteSpace(personaModel) ? personaModel
        : null;

    /// <summary>
    /// UNION the persona's tools with the node's, preserving the tri-state: if either side is null (inherit the
    /// harness default), the other side wins as-is; when both are present, the union is order-stable and de-duped
    /// (persona tools first, then any node tools not already present). Tools SUPPLEMENT, never narrow — picking a
    /// persona and adding a tool inline grants both.
    /// </summary>
    internal static IReadOnlyList<string>? MergeTools(IReadOnlyList<string>? personaTools, IReadOnlyList<string>? nodeTools)
    {
        if (personaTools is null) return nodeTools;
        if (nodeTools is null) return personaTools;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();

        foreach (var tool in personaTools.Concat(nodeTools))
            if (!string.IsNullOrWhiteSpace(tool) && seen.Add(tool))
                merged.Add(tool);

        return merged;
    }

    /// <summary>Deserialize the persona's jsonb tool list — null/blank = inherit the harness default (distinct from "[]" = no tools).</summary>
    internal static IReadOnlyList<string>? ParseTools(string? toolsJson) =>
        string.IsNullOrWhiteSpace(toolsJson) ? null : JsonSerializer.Deserialize<List<string>>(toolsJson, AgentJson.Options);
}
