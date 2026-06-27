using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Skills;

/// <summary>
/// Pins the harness-neutral skill projection: resolved <see cref="AgentSkill"/>s → <c>&lt;root&gt;/&lt;slug&gt;/SKILL.md</c>
/// ConfigHomeFiles with valid frontmatter. The load-bearing guarantee is that a description carrying YAML-hostile
/// characters (colons, quotes, the embedded <c>&lt;example&gt;</c> blocks contains-studio agents use) round-trips
/// through the parser unchanged — a naive string-concat frontmatter would corrupt it.
/// </summary>
[Trait("Category", "Unit")]
public class SkillProjectionTests
{
    private static readonly ClaudeCodeSkillParser Parser = new();

    [Fact]
    public void Projects_each_skill_to_a_skill_md_under_the_root()
    {
        var files = SkillProjection.ToConfigHomeFiles(new[]
        {
            new AgentSkill { Slug = "tdd", Description = "Use when implementing.", Body = "Write the test first." },
            new AgentSkill { Slug = "debugging", Description = "Use when stuck.", Body = "Form a hypothesis." },
        }, "skills");

        files.Select(f => f.RelativePath).ShouldBe(new[] { "skills/tdd/SKILL.md", "skills/debugging/SKILL.md" });
    }

    [Fact]
    public void Serialized_skill_round_trips_through_the_parser()
    {
        var file = SkillProjection.ToConfigHomeFiles(new[]
        {
            new AgentSkill { Slug = "tdd", Description = "Use when implementing any feature.", Body = "# TDD\n\nWrite the test first." },
        }, "skills").Single();

        var parsed = Parser.Parse(file.Content, file.RelativePath);

        parsed.Name.ShouldBe("tdd");
        parsed.Description.ShouldBe("Use when implementing any feature.");
        parsed.Body.ShouldBe("# TDD\n\nWrite the test first.");
        parsed.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void A_yaml_hostile_description_survives_the_round_trip()
    {
        // contains-studio-style: a description that embeds colons, quotes, and <example> blocks with newlines.
        const string hostile = "Use this agent when X. Examples:\n<example>\nuser: \"do: a thing\"\nassistant: \"sure\"\n</example>";

        var file = SkillProjection.ToConfigHomeFiles(new[]
        {
            new AgentSkill { Slug = "tricky", Description = hostile, Body = "Body." },
        }, "skills").Single();

        var parsed = Parser.Parse(file.Content, file.RelativePath);

        parsed.Diagnostics.ShouldBeEmpty(customMessage: "the frontmatter must stay valid YAML even with colons/quotes/newlines in the description");
        parsed.Description.ShouldBe(hostile);
    }

    [Fact]
    public void Null_or_empty_skills_produce_no_files()
    {
        SkillProjection.ToConfigHomeFiles(null, "skills").ShouldBeEmpty();
        SkillProjection.ToConfigHomeFiles(Array.Empty<AgentSkill>(), "skills").ShouldBeEmpty();
    }

    [Fact]
    public void A_blank_slug_is_skipped()
    {
        var files = SkillProjection.ToConfigHomeFiles(new[]
        {
            new AgentSkill { Slug = "  ", Description = "x", Body = "y" },
            new AgentSkill { Slug = "ok", Description = "x", Body = "y" },
        }, "skills");

        files.Select(f => f.RelativePath).ShouldBe(new[] { "skills/ok/SKILL.md" });
    }

    [Fact]
    public void A_null_description_serializes_to_an_empty_one_and_still_parses()
    {
        var file = SkillProjection.ToConfigHomeFiles(new[]
        {
            new AgentSkill { Slug = "no-desc", Description = null, Body = "Body." },
        }, "skills").Single();

        var parsed = Parser.Parse(file.Content, file.RelativePath);
        parsed.Name.ShouldBe("no-desc");
        parsed.Body.ShouldBe("Body.");
    }
}
