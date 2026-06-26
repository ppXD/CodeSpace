using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Skills;

/// <summary>
/// Pins the skill-parser registry: resolve-by-kind, a clear throw for an unregistered kind, and the
/// fail-loud guard against two parsers claiming the same kind. Mirrors the agent-parser/harness registries.
/// </summary>
[Trait("Category", "Unit")]
public class SkillArtifactParserRegistryTests
{
    private sealed class FakeParser : ISkillArtifactParser
    {
        public FakeParser(string kind) { Kind = kind; }
        public string Kind { get; }
        public ParsedSkillDefinition Parse(string fileText, string sourcePath) => new() { SourcePath = sourcePath };
    }

    [Fact]
    public void Resolves_a_registered_parser_by_kind()
    {
        var registry = new SkillArtifactParserRegistry(new ISkillArtifactParser[] { new FakeParser("claude-code") });

        registry.Resolve("claude-code").Kind.ShouldBe("claude-code");
        registry.All.Count.ShouldBe(1);
    }

    [Fact]
    public void Throws_for_an_unregistered_kind()
    {
        var registry = new SkillArtifactParserRegistry(new ISkillArtifactParser[] { new FakeParser("claude-code") });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("aider")).Message.ShouldContain("aider");
    }

    [Fact]
    public void Throws_when_two_parsers_claim_the_same_kind()
    {
        var dup = new ISkillArtifactParser[] { new FakeParser("claude-code"), new FakeParser("claude-code") };

        Should.Throw<InvalidOperationException>(() => new SkillArtifactParserRegistry(dup)).Message.ShouldContain("claude-code");
    }
}
