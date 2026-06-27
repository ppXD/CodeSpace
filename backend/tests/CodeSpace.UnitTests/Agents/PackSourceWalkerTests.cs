using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Parsers.ClaudeCode;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the recursive pack discovery: skills are every <c>SKILL.md</c>; agents are other <c>.md</c> with a
/// frontmatter <c>name</c> that are NOT inside a skill directory; READMEs/docs (no name) and a skill's own
/// resource markdown are excluded; discovery is recursive (plugin-style nested layouts), deterministically
/// ORDERED (assertions below pin raw emission order, not a re-sorted view), and resilient (a single
/// malformed/unreadable file never aborts the walk). Source paths are root-relative + forward-slashed.
/// </summary>
[Trait("Category", "Unit")]
public class PackSourceWalkerTests
{
    private static readonly PackSourceWalker Walker = new(
        new AgentArtifactParserRegistry(new[] { new ClaudeCodeAgentParser() }),
        new SkillArtifactParserRegistry(new[] { new ClaudeCodeSkillParser() }));

    [Fact]
    public async Task Discovers_agents_and_skills_recursively_in_deterministic_order_and_excludes_non_agents()
    {
        using var pack = new TempPack();
        // Agents in three real-world layouts: a flat agents/ dir, a contains-studio-style file at the root, and a
        // wshobson-style plugin subtree.
        pack.Agent("agents/backend-architect.md", "backend-architect", "Use PROACTIVELY for system design.");
        pack.Agent("rapid-prototyper.md", "rapid-prototyper", "Use this agent to scaffold an MVP.");
        pack.Agent("plugins/python/agents/python-pro.md", "python-pro", "Master modern Python.");
        // Skills: a top-level skill + a plugin skill.
        pack.Skill("skills/test-driven-development/SKILL.md", "test-driven-development", "Use when implementing.");
        pack.Skill("plugins/python/skills/pytest-patterns/SKILL.md", "pytest-patterns", "Use when writing tests.");
        // Excluded: a README (no frontmatter), and a skill's own resource markdown that EVEN HAS a name (the
        // skill-directory exclusion, not just the name check, must keep it out of agents).
        pack.File("README.md", "# A pack\n\nNo frontmatter here.");
        pack.Agent("skills/test-driven-development/references/details.md", "sneaky-not-an-agent", "x");

        var result = await Walker.WalkAsync(pack.Root, default);

        // NO re-sort: pin the DETERMINISTIC emitted order (Ordinal-sorted file paths). A regression that dropped the
        // production sort would emit in raw enumeration order and fail here.
        result.Agents.Select(a => a.SourcePath).ShouldBe(new[]
        {
            "agents/backend-architect.md",
            "plugins/python/agents/python-pro.md",
            "rapid-prototyper.md",
        });
        result.Skills.Select(s => s.SourcePath).ShouldBe(new[]
        {
            "plugins/python/skills/pytest-patterns/SKILL.md",
            "skills/test-driven-development/SKILL.md",
        });

        result.Agents.ShouldNotContain(a => a.SourcePath.Contains("references/details.md"), "a skill's own resource markdown is not an agent even with a name");
        result.Agents.ShouldNotContain(a => a.SourcePath == "README.md", "a README with no frontmatter is not an agent");

        // Source paths are always forward-slashed regardless of OS separator.
        result.Agents.ShouldAllBe(a => !a.SourcePath.Contains('\\'));
        result.Skills.ShouldAllBe(s => !s.SourcePath.Contains('\\'));
    }

    [Fact]
    public async Task A_root_level_skill_does_not_suppress_agents()
    {
        using var pack = new TempPack();
        // A single-skill-repo layout: SKILL.md at the ROOT. It must register as a skill WITHOUT swallowing the tree
        // (a root skill dir would otherwise match every file in IsUnderAnySkillDir and drop all agents).
        pack.Skill("SKILL.md", "root-skill", "Use always.");
        pack.Agent("agents/backend.md", "backend", "Use for backend.");
        pack.Agent("reviewer.md", "reviewer", "Use to review.");

        var result = await Walker.WalkAsync(pack.Root, default);

        result.Skills.Select(s => s.SourcePath).ShouldBe(new[] { "SKILL.md" });
        result.Agents.Select(a => a.SourcePath).ShouldBe(new[] { "agents/backend.md", "reviewer.md" },
            customMessage: "a root-level SKILL.md must NOT suppress sibling/descendant agents");
    }

    [Fact]
    public async Task A_malformed_skill_rides_along_but_a_nameless_md_is_not_an_agent()
    {
        using var pack = new TempPack();
        pack.File("skills/broken/SKILL.md", "---\nname: \"unterminated\n---\nbody");   // invalid YAML
        pack.File("docs/nameless.md", "---\ndescription: has a description but no name\n---\nbody");

        var result = await Walker.WalkAsync(pack.Root, default);

        // The malformed SKILL.md still appears (it IS a skill) carrying its parse diagnostics — it does not abort the walk.
        var broken = result.Skills.Single(s => s.SourcePath == "skills/broken/SKILL.md");
        broken.Diagnostics.ShouldNotBeEmpty("a malformed SKILL.md rides along with diagnostics rather than aborting discovery");

        // A .md with no name is NOT classified as an agent (correctly excluded — it's a doc, not a ridden-along error).
        result.Agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_prefix_sibling_of_a_skill_dir_is_still_an_agent()
    {
        using var pack = new TempPack();
        pack.Skill("skills/tdd/SKILL.md", "tdd", "Use when implementing.");
        pack.Agent("skills/tdd-extra/agent.md", "tdd-extra-agent", "Use for the extra thing.");   // sibling dir sharing the prefix "tdd"

        var result = await Walker.WalkAsync(pack.Root, default);

        result.Agents.Select(a => a.SourcePath).ShouldBe(new[] { "skills/tdd-extra/agent.md" },
            customMessage: "skills/tdd-extra is a prefix SIBLING of the skill dir skills/tdd — the separator guard must not treat it as under the skill");
    }

    [Fact]
    public async Task Skill_filename_match_is_case_insensitive()
    {
        using var pack = new TempPack();
        pack.Skill("skills/lower/skill.md", "lower-skill", "Use always.");   // lowercase filename

        var result = await Walker.WalkAsync(pack.Root, default);

        result.Skills.Select(s => s.SourcePath).ShouldBe(new[] { "skills/lower/skill.md" }, customMessage: "SKILL.md matching is OrdinalIgnoreCase");
        result.Agents.ShouldBeEmpty("a lowercase skill.md is a skill, not an agent");
    }

    [Fact]
    public async Task An_unreadable_file_does_not_abort_the_walk()
    {
        if (OperatingSystem.IsWindows()) return;   // broken-symlink trigger is POSIX

        using var pack = new TempPack();
        pack.Agent("agents/good.md", "good", "Use this one.");
        pack.BrokenSymlink("skills/ghost/SKILL.md");   // a SKILL.md symlink to a nonexistent target → read throws

        var result = await Walker.WalkAsync(pack.Root, default);

        // The unreadable SKILL.md rides along with a read diagnostic — the walk did NOT abort.
        var ghost = result.Skills.Single(s => s.SourcePath == "skills/ghost/SKILL.md");
        ghost.Diagnostics.ShouldNotBeEmpty();
        // …and the good agent was still discovered (the bad file did not lose the rest of the pack).
        result.Agents.Select(a => a.SourcePath).ShouldBe(new[] { "agents/good.md" });
    }

    [Fact]
    public async Task Parses_each_artifact_so_the_preview_has_its_structure()
    {
        using var pack = new TempPack();
        pack.Agent("agents/reviewer.md", "reviewer", "Use to review PRs.");
        pack.Skill("skills/tdd/SKILL.md", "tdd", "Use when implementing.");

        var result = await Walker.WalkAsync(pack.Root, default);

        var agent = result.Agents.Single();
        agent.Name.ShouldBe("reviewer");
        agent.Description.ShouldBe("Use to review PRs.");

        var skill = result.Skills.Single();
        skill.Name.ShouldBe("tdd");
        skill.Description.ShouldBe("Use when implementing.");
    }

    [Fact]
    public async Task An_empty_tree_discovers_nothing()
    {
        using var pack = new TempPack();

        var result = await Walker.WalkAsync(pack.Root, default);

        result.Agents.ShouldBeEmpty();
        result.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_directory_throws()
    {
        await Should.ThrowAsync<DirectoryNotFoundException>(() => Walker.WalkAsync(Path.Combine(Path.GetTempPath(), "cs-nope-" + Guid.NewGuid().ToString("N")), default));
    }

    private sealed class TempPack : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cs-pack-" + Guid.NewGuid().ToString("N"));

        public TempPack() => Directory.CreateDirectory(Root);

        public void Agent(string relPath, string name, string description) =>
            File(relPath, $"---\nname: {name}\ndescription: {description}\n---\nYou are {name}.");

        public void Skill(string relPath, string name, string description) =>
            File(relPath, $"---\nname: {name}\ndescription: {description}\n---\n# {name}\n\nInstructions.");

        public void File(string relPath, string content)
        {
            var full = Full(relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
        }

        public void BrokenSymlink(string relPath)
        {
            var full = Full(relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.CreateSymbolicLink(full, Path.Combine(Root, "does-not-exist-" + Guid.NewGuid().ToString("N")));
        }

        private string Full(string relPath) => Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
