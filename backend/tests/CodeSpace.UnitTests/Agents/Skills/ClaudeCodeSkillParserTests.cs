using System.Text.Json;
using CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Skills;

/// <summary>
/// Pins the Claude-Code / Agent-Skills SKILL.md parser. The load-bearing guarantee is LOSSLESS forward-compat:
/// RawFrontmatterJson must preserve the original frontmatter verbatim (unknown / nested / list / typed keys
/// included) so a future key needs no migration. Also pins the thin Level-1 index (name/description/category),
/// the body split, and tolerant handling of malformed input (diagnostics, never throws). Mirrors
/// ClaudeCodeAgentParserTests.
/// </summary>
[Trait("Category", "Unit")]
public class ClaudeCodeSkillParserTests
{
    private static readonly ClaudeCodeSkillParser Parser = new();

    [Fact]
    public void Kind_is_claude_code() => Parser.Kind.ShouldBe("claude-code");

    [Fact]
    public void Parses_the_thin_index_and_body_from_a_well_formed_skill()
    {
        const string md =
            "---\n" +
            "name: test-driven-development\n" +
            "description: Use when implementing any feature or bugfix, before writing implementation code\n" +
            "category: testing\n" +
            "---\n" +
            "# Test-Driven Development\n\nWrite the test first. Watch it fail.\n";

        var s = Parser.Parse(md, "skills/test-driven-development/SKILL.md");

        s.SourcePath.ShouldBe("skills/test-driven-development/SKILL.md");
        s.Name.ShouldBe("test-driven-development");
        s.Description.ShouldBe("Use when implementing any feature or bugfix, before writing implementation code");
        s.Category.ShouldBe("testing");
        s.Body.ShouldBe("# Test-Driven Development\n\nWrite the test first. Watch it fail.", customMessage: "the body is everything after the closing fence, trimmed");
        s.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Category_is_null_when_absent()
    {
        const string md =
            "---\n" +
            "name: brainstorming\n" +
            "description: Use before any creative work\n" +
            "---\n" +
            "Body.\n";

        Parser.Parse(md, "skills/brainstorming/SKILL.md").Category.ShouldBeNull();
    }

    [Fact]
    public void Category_is_null_when_a_non_scalar()
    {
        const string md =
            "---\n" +
            "name: x\n" +
            "description: Use when\n" +
            "category:\n" +
            "  - a\n" +
            "  - b\n" +
            "---\n" +
            "Body.\n";

        Parser.Parse(md, "skills/x/SKILL.md").Category.ShouldBeNull(customMessage: "a list value isn't a scalar — category drops to null");
    }

    [Fact]
    public void Category_is_null_when_blank()
    {
        const string md =
            "---\n" +
            "name: x\n" +
            "description: Use when\n" +
            "category: \"\"\n" +
            "---\n" +
            "Body.\n";

        Parser.Parse(md, "skills/x/SKILL.md").Category.ShouldBeNull(customMessage: "a blank scalar is coerced to null");
    }

    [Fact]
    public void Preserves_unknown_and_nested_frontmatter_keys_verbatim_in_raw_json()
    {
        // The forward-compat contract: keys we DON'T index (incl. nested maps + lists + typed scalars) must
        // survive untouched in RawFrontmatterJson so a future key needs no migration.
        const string md =
            "---\n" +
            "name: futuristic\n" +
            "description: Use when the future arrives\n" +
            "allowed-tools: Read, Grep\n" +
            "license: MIT\n" +
            "metadata:\n" +
            "  type: technique\n" +
            "  tags:\n" +
            "    - a\n" +
            "    - b\n" +
            "---\n" +
            "Body.\n";

        var s = Parser.Parse(md, "skills/futuristic/SKILL.md");

        var root = JsonDocument.Parse(s.RawFrontmatterJson).RootElement;
        root.GetProperty("allowed-tools").GetString().ShouldBe("Read, Grep");
        root.GetProperty("license").GetString().ShouldBe("MIT");
        root.GetProperty("metadata").GetProperty("type").GetString().ShouldBe("technique");
        root.GetProperty("metadata").GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void Missing_name_is_a_diagnostic_not_a_throw()
    {
        const string md =
            "---\n" +
            "description: Use when nameless\n" +
            "---\n" +
            "Body.\n";

        var s = Parser.Parse(md, "skills/x/SKILL.md");

        s.Name.ShouldBe("");
        s.Diagnostics.ShouldContain(d => d.Contains("name"));
    }

    [Fact]
    public void Missing_description_is_a_diagnostic()
    {
        const string md =
            "---\n" +
            "name: silent\n" +
            "---\n" +
            "Body.\n";

        var s = Parser.Parse(md, "skills/silent/SKILL.md");

        s.Name.ShouldBe("silent");
        s.Diagnostics.ShouldContain(d => d.Contains("description"));
    }

    [Fact]
    public void No_frontmatter_is_a_diagnostic_and_keeps_the_whole_text_as_body()
    {
        const string md = "# Just a heading\n\nNo frontmatter here.\n";

        var s = Parser.Parse(md, "skills/raw/SKILL.md");

        s.Name.ShouldBe("");
        s.Body.ShouldBe("# Just a heading\n\nNo frontmatter here.");
        s.Diagnostics.ShouldContain(d => d.Contains("frontmatter"));
    }

    [Fact]
    public void Invalid_yaml_is_a_diagnostic_not_a_throw()
    {
        const string md =
            "---\n" +
            "name: broken\n" +
            "description: \"unterminated\n" +
            "---\n" +
            "Body.\n";

        var s = Parser.Parse(md, "skills/broken/SKILL.md");

        s.Diagnostics.ShouldContain(d => d.Contains("YAML"));
    }
}
