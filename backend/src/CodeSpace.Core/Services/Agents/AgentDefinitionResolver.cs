using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IAgentDefinitionResolver"/>. Loads the referenced persona team-scoped (mirroring
/// <c>RepositoryWorkspaceResolver</c>) and merges it into the task with the locked precedence:
/// <list type="bullet">
///   <item>prompt — persona system prompt PREPENDED to the node goal (the harness has one prompt conduit, the goal);</item>
///   <item>model — a non-blank node override wins, else the persona's model, else null (harness default);</item>
///   <item>tools — the persona's tools UNIONED with the node's (supplement-never-narrow; null = inherit the harness default);</item>
///   <item>skills — the persona's bound skills (the <c>AgentSkillBinding</c> join → active skills) frozen onto the task; the harness projects them into its native SKILL.md layout;</item>
///   <item>everything else (harness, runner, repo, permissions, timeout) is node-authored and flows through.</item>
/// </list>
/// MCP / autonomy are NOT merged yet — the task envelope + harness don't carry them, so merging would silently
/// drop them; those land with their own projection in a follow-up.
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
        // A picked credentialed model expands FIRST — into the loose Model + ModelCredentialId — so it applies to
        // BOTH an inline run (which returns just below) and a persona run (where it then takes node-level precedence
        // over the persona). No reference set → unchanged, byte-identical.
        task = await ApplyCredentialedModelAsync(task, teamId, cancellationToken).ConfigureAwait(false);

        if (task.AgentDefinitionId is not { } id) return task;   // pure-inline run — unchanged, byte-for-byte today's behavior

        var persona = await LoadPersonaAsync(id, teamId, cancellationToken).ConfigureAwait(false);

        var goal = ComposeGoal(persona.SystemPrompt, task.Goal);

        if (string.IsNullOrWhiteSpace(goal))
            throw new AgentDefinitionResolutionException(
                $"Agent persona '{persona.Slug}' has an empty system prompt and the node supplied no goal — nothing to run.");

        var model = ResolveModel(task.Model, persona.Model);

        var tools = MergeTools(ParseTools(persona.ToolsJson), task.Tools);

        var modelCredentialId = ResolveModelCredentialId(task.ModelCredentialId, persona.ModelCredentialId);

        var skills = await LoadSkillsAsync(id, teamId, cancellationToken).ConfigureAwait(false);

        return task with { Goal = goal, Model = model, Tools = tools, ModelCredentialId = modelCredentialId, Skills = skills };
    }

    /// <summary>
    /// Load the persona's bound skills (the <c>AgentSkillBinding</c> join → ACTIVE skills) as the resolved payload
    /// frozen onto the task, ordered by bind time then slug. The slug tiebreak makes the order DETERMINISTIC: skills
    /// bound in one <c>SetForAgent</c> call share a timestamp, so without it the persisted task_json's <c>Skills</c>
    /// order could differ across two dispatches of the same persona. A soft-deleted skill drops out (the join filters
    /// on <c>DeletedDate == null</c>). Returns null when the persona has no skills, so the task_json stays
    /// byte-identical to a pre-skills envelope (the field is <c>[JsonIgnore(WhenWritingNull)]</c>).
    /// </summary>
    private async Task<IReadOnlyList<AgentSkill>?> LoadSkillsAsync(Guid agentId, Guid teamId, CancellationToken cancellationToken)
    {
        var skills = await _db.AgentSkillBinding.AsNoTracking()
            .Where(b => b.AgentDefinitionId == agentId)
            .Join(_db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && s.DeletedDate == null),
                b => b.SkillDefinitionId, s => s.Id, (b, s) => new { b.CreatedDate, s })
            .OrderBy(x => x.CreatedDate).ThenBy(x => x.s.Slug)
            .Select(x => new AgentSkill { Slug = x.s.Slug, Description = x.s.Description, Body = x.s.Body })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return skills.Count == 0 ? null : skills;
    }

    /// <summary>
    /// Expand a picked <c>ModelCredentialModel</c> reference into the loose Model + ModelCredentialId — the operator
    /// chose one concrete credentialed model, so it sets BOTH axes (a model id paired with its backing credential).
    /// The row must be ENABLED and under an ACTIVE, non-deleted credential of the run's team (the same scoping the
    /// credential resolver uses); a missing / disabled / revoked one FAILS CLOSED (a clean node failure) rather than
    /// silently falling back — the operator's explicit choice is honoured or surfaced, never substituted. A disabled
    /// model is "not part of the usable pool", so a pinned-then-disabled model fails rather than runs. No reference →
    /// the task is returned untouched (byte-identical).
    /// </summary>
    private async Task<AgentTask> ApplyCredentialedModelAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.ModelCredentialModelId is not { } rowId) return task;

        var row = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Id == rowId && m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.ModelId, m.ModelCredentialId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AgentDefinitionResolutionException($"Selected model {rowId} is not an active, enabled credentialed model for this team.");

        return task with { Model = row.ModelId, ModelCredentialId = row.ModelCredentialId };
    }

    public async Task<Guid?> ResolveSlugAsync(string slug, Guid teamId, CancellationToken cancellationToken)
    {
        // Normalize the handle the SAME way authoring derives a slug from a name, so a brain that authored a display
        // name ("Security Reviewer") or the exact handle ("security-reviewer") both resolve. A handle that yields no
        // slug → no match (null). Team-scoped + non-deleted, mirroring LoadPersonaAsync so a foreign / soft-deleted
        // slug never resolves. Scoped to WORKING: a @-mention resolves only to a runnable bench persona, never to a
        // Library STORE snapshot — and a snapshot may legally share a Working row's handle (the team-slug uniqueness
        // is Working-only), so without this filter SingleOrDefault could match two rows and throw.
        var normalized = AgentDefinitionService.DeriveSlug(slug);

        if (normalized.Length == 0) return null;

        return await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.Slug == normalized && a.Scope == DefinitionScope.Working && a.DeletedDate == null)
            .Select(a => (Guid?)a.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Node-pinned credential reference wins, else the persona's default, else null = fall back to a team/operator key at resolve time.</summary>
    internal static Guid? ResolveModelCredentialId(Guid? nodeRef, Guid? personaRef) => nodeRef ?? personaRef;

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

    /// <summary>
    /// Deserialize the persona's jsonb tool list — null/blank = inherit the harness default (distinct from
    /// "[]" = no tools). A corrupt blob surfaces as an <see cref="AgentDefinitionResolutionException"/> so a
    /// bad imported persona fails as a clean node failure, not a raw <see cref="JsonException"/> escaping the
    /// resolve path.
    /// </summary>
    internal static IReadOnlyList<string>? ParseTools(string? toolsJson)
    {
        if (string.IsNullOrWhiteSpace(toolsJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(toolsJson, AgentJson.Options);
        }
        catch (JsonException ex)
        {
            throw new AgentDefinitionResolutionException($"Agent persona has an unreadable tools list: {ex.Message}", ex);
        }
    }
}
