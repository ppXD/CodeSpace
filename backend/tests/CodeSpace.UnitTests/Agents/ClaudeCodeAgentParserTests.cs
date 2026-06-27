using System.Text.Json;
using CodeSpace.Core.Services.Agents.Parsers.ClaudeCode;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the Claude-Code subagent parser. The load-bearing guarantee is LOSSLESS forward-compat:
/// RawFrontmatterJson must preserve the original frontmatter verbatim (unknown / nested / list / typed
/// keys included) so a future key needs no migration. Also pins the thin-index projection, the fence
/// split, the tools tri-state (null vs []), and tolerant handling of malformed input (diagnostics, never
/// throws).
/// </summary>
[Trait("Category", "Unit")]
public class ClaudeCodeAgentParserTests
{
    private static readonly ClaudeCodeAgentParser Parser = new();

    [Fact]
    public void Kind_is_claude_code() => Parser.Kind.ShouldBe("claude-code");

    [Fact]
    public void Parses_the_thin_index_and_body_from_a_well_formed_subagent()
    {
        const string md =
            "---\n" +
            "name: backend-architect\n" +
            "description: Use PROACTIVELY for system design.\n" +
            "model: claude-opus-4-8\n" +
            "tools: Read, Grep, Bash\n" +
            "---\n" +
            "You are a senior backend architect.\n\nThink before coding.\n";

        var p = Parser.Parse(md, "agents/backend-architect.md");

        p.SourcePath.ShouldBe("agents/backend-architect.md");
        p.Name.ShouldBe("backend-architect");
        p.Description.ShouldBe("Use PROACTIVELY for system design.");
        p.Model.ShouldBe("claude-opus-4-8");
        p.Tools.ShouldBe(new[] { "Read", "Grep", "Bash" }, customMessage: "Claude-Code writes tools as a comma-separated scalar — split into a list");
        p.SystemPrompt.ShouldBe("You are a senior backend architect.\n\nThink before coding.", customMessage: "the body is everything after the closing fence, trimmed");
        p.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Tolerates_non_strict_yaml_frontmatter_and_recovers_the_agent()
    {
        // The exact real-world shape from contains-studio/agents (caught by the real-GitHub E2E): the description is
        // a single unquoted line embedding <example> blocks with `Context:` / `user: "…"` — colon-space sequences that
        // make strict YAML throw ("found invalid mapping"). Without tolerant parsing the whole agent is lost (no name
        // → excluded from discovery). The lenient fallback must recover the top-level scalars.
        const string md =
            "---\n" +
            "name: backend-architect\n" +
            "description: Use this agent when designing APIs. Examples:\\n\\n<example>\\nContext: Designing a new API\\nuser: \"We need an API\"\\nassistant: \"I'll design it.\"\\n</example>\n" +
            "color: purple\n" +
            "tools: Write, Read, Bash\n" +
            "---\n" +
            "You are a backend architect.\n";

        var p = Parser.Parse(md, "engineering/backend-architect.md");

        p.Name.ShouldBe("backend-architect", customMessage: "the agent must NOT be dropped just because its description isn't strict YAML");
        p.Tools.ShouldBe(new[] { "Write", "Read", "Bash" });
        p.SystemPrompt.ShouldBe("You are a backend architect.");
        p.Diagnostics.ShouldContain(d => d.Contains("not strict YAML"), customMessage: "the lenient fallback is surfaced as a diagnostic, but the agent is still importable");
    }

    [Fact]
    public void Preserves_unknown_and_nested_frontmatter_keys_verbatim_in_raw_json()
    {
        // The forward-compat contract: keys we DON'T index (incl. nested maps + lists + typed scalars) must
        // survive untouched in RawFrontmatterJson so a future key needs no migration.
        const string md =
            "---\n" +
            "name: futuristic\n" +
            "color: blue\n" +
            "max_turns: 42\n" +
            "metadata:\n" +
            "  team: platform\n" +
            "  tags:\n" +
            "    - a\n" +
            "    - b\n" +
            "---\n" +
            "body\n";

        var raw = JsonDocument.Parse(Parser.Parse(md, "x.md").RawFrontmatterJson).RootElement;

        raw.GetProperty("color").GetString().ShouldBe("blue");
        raw.GetProperty("max_turns").GetString().ShouldBe("42", customMessage: "scalars round-trip (string form is fine — the point is lossless, not typed)");
        raw.GetProperty("metadata").GetProperty("team").GetString().ShouldBe("platform", customMessage: "a NESTED map survives — the reason a real YAML parser is used, not a line-splitter");
        raw.GetProperty("metadata").GetProperty("tags").GetArrayLength().ShouldBe(2, customMessage: "a nested list survives verbatim");
    }

    [Fact]
    public void Tools_as_a_yaml_list_is_also_supported()
    {
        const string md = "---\nname: n\ntools:\n  - Read\n  - Grep\n---\nbody\n";

        Parser.Parse(md, "x.md").Tools.ShouldBe(new[] { "Read", "Grep" });
    }

    [Fact]
    public void Tools_absent_is_null_present_but_empty_is_empty()
    {
        Parser.Parse("---\nname: a\n---\nbody\n", "a.md").Tools
            .ShouldBeNull("tools key absent → null = inherit the harness default toolset");

        Parser.Parse("---\nname: b\ntools: \"\"\n---\nbody\n", "b.md").Tools
            .ShouldBeEmpty("tools present-but-empty → [] = no tools (distinct from the default)");
    }

    [Fact]
    public void Model_blank_is_null_so_the_harness_default_applies()
    {
        Parser.Parse("---\nname: a\nmodel: \"\"\n---\nbody\n", "a.md").Model.ShouldBeNull();
        Parser.Parse("---\nname: a\n---\nbody\n", "a.md").Model.ShouldBeNull();
    }

    [Fact]
    public void A_file_with_no_frontmatter_is_body_only_with_a_diagnostic()
    {
        var p = Parser.Parse("Just a prompt, no frontmatter.\n", "loose.md");

        p.Name.ShouldBe("");
        p.SystemPrompt.ShouldBe("Just a prompt, no frontmatter.");
        p.Diagnostics.ShouldNotBeEmpty("an un-named artifact is previewable but flagged un-importable, never a crash");
    }

    [Fact]
    public void A_missing_name_yields_a_diagnostic_not_an_exception()
    {
        var p = Parser.Parse("---\ndescription: no name here\n---\nbody\n", "x.md");

        p.Name.ShouldBe("");
        p.Description.ShouldBe("no name here");
        p.Diagnostics.ShouldContain(d => d.Contains("name"), customMessage: "the missing required name is reported so the preview can mark it un-importable");
    }

    [Fact]
    public void Invalid_yaml_frontmatter_recovers_the_name_leniently_with_a_diagnostic()
    {
        // Malformed YAML must neither throw NOR silently drop a NAMED artifact: the lenient fallback recovers the
        // top-level name (so an agent stays importable) + flags a diagnostic. This is the discovery-admission contract.
        var p = Parser.Parse("---\nname: [unclosed\n  bad: : :\n---\nbody\n", "x.md");

        p.Name.ShouldBe("[unclosed", customMessage: "the lenient fallback recovers the top-level name from non-strict YAML");
        p.Diagnostics.ShouldContain(d => d.Contains("not strict YAML"));
    }

    [Fact]
    public void Malformed_yaml_with_no_name_stays_un_importable()
    {
        // The negative of the recovery contract: the fallback must NOT fabricate a name. A malformed block with no
        // parseable name line yields Name="" so the artifact is excluded from discovery (not admitted as a junk agent).
        var p = Parser.Parse("---\ndescription: \"unterminated\n---\nbody\n", "x.md");

        p.Name.ShouldBe("");
        p.Diagnostics.ShouldNotBeEmpty();
    }
}
