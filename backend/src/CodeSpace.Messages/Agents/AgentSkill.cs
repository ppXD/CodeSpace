namespace CodeSpace.Messages.Agents;

/// <summary>
/// One Skill resolved onto an <see cref="AgentTask"/> — the harness-neutral payload a harness projects into its
/// own native skills layout (a <c>SKILL.md</c> under the per-run config home, where the CLI's native loader does
/// the progressive disclosure). Resolved from the persona's <c>AgentSkillBinding</c> join at dispatch and frozen
/// into the task, so a run is version-pinned to the skills as they were when it started. Carries only what the
/// projection needs: the handle (directory + frontmatter identifier), the trigger description (Level 1), and the
/// instruction body (Level 2). Bundled Level-3 resources are a later refinement.
/// </summary>
public sealed record AgentSkill
{
    /// <summary>The skill handle — used as both the skill directory name and the <c>SKILL.md</c> frontmatter <c>name</c>.</summary>
    public required string Slug { get; init; }

    /// <summary>The trigger/router text (frontmatter <c>description</c>) — when the skill applies. Empty when the source had none.</summary>
    public string? Description { get; init; }

    /// <summary>The SKILL.md instruction body (Level 2), loaded by the CLI only when the skill is triggered.</summary>
    public string Body { get; init; } = "";
}
