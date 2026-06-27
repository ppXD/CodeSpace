using CodeSpace.Messages.Agents;
using YamlDotNet.Serialization;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Projects resolved <see cref="AgentSkill"/>s into the on-disk <c>SKILL.md</c> artifacts a coding-agent CLI
/// discovers natively. Harness-NEUTRAL: every supported harness uses the byte-identical Agent-Skills format
/// (frontmatter <c>name</c> + <c>description</c>, then the body) with progressive disclosure — the ONLY thing
/// that differs per harness is the skills-root directory under the per-run config home (Claude scans
/// <c>skills/</c> under <c>CLAUDE_CONFIG_DIR</c>, Codex under <c>CODEX_HOME</c>). So each
/// <c>IAgentHarness.BuildInvocation</c> calls this with its own root and gets <see cref="ConfigHomeFile"/>s the
/// runner materializes. The frontmatter is serialized with a real YAML writer so a description carrying colons /
/// quotes / newlines (e.g. embedded <c>&lt;example&gt;</c> blocks) can never produce malformed frontmatter.
/// </summary>
public static class SkillProjection
{
    private static readonly ISerializer Yaml = new SerializerBuilder().Build();

    /// <summary>
    /// Build the <see cref="ConfigHomeFile"/>s for <paramref name="skills"/> as <c>&lt;skillsRoot&gt;/&lt;slug&gt;/SKILL.md</c>.
    /// Null/empty skills → no files. A skill with a blank slug is skipped (it could not address a directory).
    /// </summary>
    public static IReadOnlyList<ConfigHomeFile> ToConfigHomeFiles(IReadOnlyList<AgentSkill>? skills, string skillsRoot)
    {
        if (skills is null || skills.Count == 0) return Array.Empty<ConfigHomeFile>();

        var files = new List<ConfigHomeFile>(skills.Count);

        foreach (var skill in skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Slug)) continue;

            files.Add(new ConfigHomeFile
            {
                RelativePath = $"{skillsRoot}/{skill.Slug}/SKILL.md",
                Content = SerializeSkillMarkdown(skill),
            });
        }

        return files;
    }

    /// <summary>Render one skill as a <c>SKILL.md</c>: a YAML frontmatter fence (<c>name</c> = slug, <c>description</c> = trigger) followed by the instruction body.</summary>
    internal static string SerializeSkillMarkdown(AgentSkill skill)
    {
        var frontmatter = new Dictionary<string, object?>
        {
            ["name"] = skill.Slug,
            ["description"] = skill.Description ?? "",
        };

        var yaml = Yaml.Serialize(frontmatter).TrimEnd('\n');
        var body = (skill.Body ?? "").Trim();

        return $"---\n{yaml}\n---\n{body}\n";
    }
}
